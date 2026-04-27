using Andy.Containers.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Andy.Containers.Configurator;

public sealed class RunConfigurator : IRunConfigurator
{
    private readonly IAndyAgentsClient _agents;
    private readonly IHeadlessConfigBuilder _builder;
    private readonly IHeadlessConfigWriter _writer;
    private readonly ITokenIssuer _tokens;
    private readonly IOptions<SecretsOptions> _secrets;
    private readonly ILogger<RunConfigurator> _logger;

    public RunConfigurator(
        IAndyAgentsClient agents,
        IHeadlessConfigBuilder builder,
        IHeadlessConfigWriter writer,
        ITokenIssuer tokens,
        IOptions<SecretsOptions> secrets,
        ILogger<RunConfigurator> logger)
    {
        _agents = agents;
        _builder = builder;
        _writer = writer;
        _tokens = tokens;
        _secrets = secrets;
        _logger = logger;
    }

    public async Task<RunConfiguratorResult> ConfigureAsync(Run run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        var spec = await _agents.GetAgentAsync(run.AgentId, run.AgentRevision, ct);
        if (spec is null)
        {
            _logger.LogWarning(
                "Configurator: agent '{AgentId}' (revision {Revision}) not found for run {RunId}.",
                run.AgentId, run.AgentRevision, run.Id);
            return RunConfiguratorResult.Fail($"Agent '{run.AgentId}' not found.");
        }

        // AP10 (rivoli-ai/andy-containers#112). Mint a run-scoped token
        // and merge ANDY_TOKEN + ANDY_PROXY_URL + ANDY_MCP_URL into the
        // env vars the headless config carries to andy-cli. Mint is
        // idempotent so a configurator retry doesn't orphan tokens.
        // Skip injecting any URL whose option value is null/empty so a
        // half-configured deployment surfaces as a missing var rather
        // than a misleading value.
        var token = await _tokens.MintAsync(run.Id, ct);
        spec = spec with { EnvVars = MergeRunSecrets(spec.EnvVars, token, _secrets.Value) };

        HeadlessRunConfig config;
        try
        {
            config = _builder.Build(run, spec);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex,
                "Configurator: builder rejected agent '{AgentId}' for run {RunId}: {Message}",
                run.AgentId, run.Id, ex.Message);
            return RunConfiguratorResult.Fail(ex.Message);
        }

        try
        {
            var path = await _writer.WriteAsync(config, ct);
            _logger.LogInformation(
                "Configurator: wrote headless config for run {RunId} to {Path}.", run.Id, path);
            return RunConfiguratorResult.Ok(path);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex,
                "Configurator: failed to write headless config for run {RunId}: {Message}",
                run.Id, ex.Message);
            return RunConfiguratorResult.Fail($"Failed to write config: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex,
                "Configurator: permission denied writing headless config for run {RunId}: {Message}",
                run.Id, ex.Message);
            return RunConfiguratorResult.Fail($"Permission denied writing config: {ex.Message}");
        }
    }

    // Merge AP10's run-scoped secrets with whatever EnvVars the agent
    // spec already carries. The agent's vars win on collision — an
    // agent author who explicitly pins ANDY_TOKEN (e.g. for a test
    // double) should not be silently overridden by the issuer; the
    // collision is intentional, not the platform's call to break.
    private static IReadOnlyDictionary<string, string> MergeRunSecrets(
        IReadOnlyDictionary<string, string>? agentEnv, RunToken token, SecretsOptions secrets)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EnvVarNames.AndyToken] = token.Token,
        };
        if (!string.IsNullOrEmpty(secrets.ProxyUrl)) merged[EnvVarNames.AndyProxyUrl] = secrets.ProxyUrl;
        if (!string.IsNullOrEmpty(secrets.McpUrl)) merged[EnvVarNames.AndyMcpUrl] = secrets.McpUrl;

        if (agentEnv is { Count: > 0 })
        {
            foreach (var (k, v) in agentEnv)
            {
                merged[k] = v;
            }
        }

        return merged;
    }
}
