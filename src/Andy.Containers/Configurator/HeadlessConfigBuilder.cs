using Andy.Containers.Models;

namespace Andy.Containers.Configurator;

/// <summary>
/// Default <see cref="IHeadlessConfigBuilder"/>. Lives in the core lib (not
/// the API project) so AP6's runner — which will live in a separate process
/// once it's spun out — can reuse the same mapper without dragging the API
/// host along.
/// </summary>
public sealed class HeadlessConfigBuilder : IHeadlessConfigBuilder
{
    // Schema enum closures. Kept private and local rather than reading the
    // schema file at runtime — the schema lives in a sibling repo and bumps
    // are deliberate version events, not silent extensions.
    private static readonly HashSet<string> AllowedProviders = new(StringComparer.Ordinal)
    {
        "anthropic", "openai", "google", "cerebras", "local",
    };

    private const string DefaultWorkspaceRoot = "/workspace";
    private const string DefaultOutputFile = "/workspace/.andy-run/output.json";
    private const string DefaultStream = "stdout";

    public HeadlessRunConfig Build(Run run, AgentSpec agent)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(agent);

        if (run.Id == Guid.Empty)
        {
            throw new ArgumentException("Run.Id must be set before building a headless config.", nameof(run));
        }

        if (string.IsNullOrWhiteSpace(agent.Instructions))
        {
            throw new ArgumentException("AgentSpec.Instructions is required (schema minLength 1).", nameof(agent));
        }

        if (string.IsNullOrWhiteSpace(agent.Model.Provider) || !AllowedProviders.Contains(agent.Model.Provider))
        {
            throw new ArgumentException(
                $"AgentSpec.Model.Provider '{agent.Model.Provider}' is not one of: {string.Join(", ", AllowedProviders)}.",
                nameof(agent));
        }

        if (string.IsNullOrWhiteSpace(agent.Model.Id))
        {
            throw new ArgumentException("AgentSpec.Model.Id is required.", nameof(agent));
        }

        var tools = agent.Tools.Select(MapTool).ToList();

        return new HeadlessRunConfig
        {
            SchemaVersion = 1,
            RunId = run.Id,
            Agent = new HeadlessAgent
            {
                Slug = agent.Slug,
                Revision = agent.Revision,
                Instructions = agent.Instructions,
                OutputFormat = agent.OutputFormat,
            },
            Model = new HeadlessModel
            {
                Provider = agent.Model.Provider,
                Id = agent.Model.Id,
                ApiKeyRef = agent.Model.ApiKeyRef,
            },
            Tools = tools,
            Workspace = new HeadlessWorkspace
            {
                Root = DefaultWorkspaceRoot,
                Branch = run.WorkspaceRef.Branch,
            },
            EnvVars = agent.EnvVars is { Count: > 0 } ? agent.EnvVars : null,
            Output = new HeadlessOutput
            {
                File = DefaultOutputFile,
                Stream = DefaultStream,
            },
            EventSink = new HeadlessEventSink
            {
                // Matches the andy.containers.events.run.{id}.{event} fan-out
                // AP6 will subscribe; ".progress" is the topic the runner
                // emits structured progress on. Other event topics under the
                // same prefix get configured at fan-in time.
                NatsSubject = $"andy.containers.events.run.{run.Id}.progress",
            },
            PolicyId = run.PolicyId,
            Boundaries = agent.Boundaries is { Count: > 0 } ? agent.Boundaries : null,
            Limits = new HeadlessLimits
            {
                MaxIterations = agent.Limits.MaxIterations,
                TimeoutSeconds = agent.Limits.TimeoutSeconds,
            },
        };
    }

    private static HeadlessTool MapTool(AgentSpecTool tool)
    {
        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            throw new ArgumentException("AgentSpecTool.Name is required.", nameof(tool));
        }

        switch (tool.Transport)
        {
            case "mcp":
                if (string.IsNullOrWhiteSpace(tool.Endpoint))
                {
                    throw new ArgumentException(
                        $"MCP tool '{tool.Name}' requires an Endpoint.", nameof(tool));
                }
                return new HeadlessTool
                {
                    Name = tool.Name,
                    Transport = "mcp",
                    Endpoint = tool.Endpoint,
                };

            case "cli":
                if (string.IsNullOrWhiteSpace(tool.Binary))
                {
                    throw new ArgumentException(
                        $"CLI tool '{tool.Name}' requires a Binary.", nameof(tool));
                }
                return new HeadlessTool
                {
                    Name = tool.Name,
                    Transport = "cli",
                    Binary = tool.Binary,
                    Command = tool.Command is { Count: > 0 } ? tool.Command : null,
                };

            default:
                throw new ArgumentException(
                    $"Tool '{tool.Name}' has unsupported transport '{tool.Transport}'. Expected 'mcp' or 'cli'.",
                    nameof(tool));
        }
    }
}
