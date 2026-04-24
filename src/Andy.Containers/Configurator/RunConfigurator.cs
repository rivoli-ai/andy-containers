using Andy.Containers.Models;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Configurator;

public sealed class RunConfigurator : IRunConfigurator
{
    private readonly IAndyAgentsClient _agents;
    private readonly IHeadlessConfigBuilder _builder;
    private readonly IHeadlessConfigWriter _writer;
    private readonly ILogger<RunConfigurator> _logger;

    public RunConfigurator(
        IAndyAgentsClient agents,
        IHeadlessConfigBuilder builder,
        IHeadlessConfigWriter writer,
        ILogger<RunConfigurator> logger)
    {
        _agents = agents;
        _builder = builder;
        _writer = writer;
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
}
