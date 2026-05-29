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
            // EX.7 (rivoli-ai/andy-containers#328). Map + validate the
            // cross-container input handoff. Empty collapses to null (same
            // posture as env_vars / boundaries) so a run without inputs
            // emits no `inputs` key — wire shape identical to pre-EX.7.
            Inputs = MapInputs(run.Inputs),
        };
    }

    // EX.7 (rivoli-ai/andy-containers#328). Validate + project the run's
    // declared inputs onto the headless config. A malformed dest path
    // throws ArgumentException here — RunConfigurator turns that into a
    // RunConfiguratorResult.Fail, so a bad handoff fails the run START
    // rather than staging into an unexpected location (or escaping the
    // inputs root). Returns null for the empty/absent case.
    private static IReadOnlyList<HeadlessInput>? MapInputs(IReadOnlyList<RunInput>? inputs)
    {
        if (inputs is not { Count: > 0 })
        {
            return null;
        }

        var mapped = new List<HeadlessInput>(inputs.Count);
        foreach (var input in inputs)
        {
            if (input.DocsRef == Guid.Empty)
            {
                throw new ArgumentException(
                    "RunInput.DocsRef must be a non-empty andy-docs document id.", nameof(inputs));
            }

            var dest = ValidateDestRelativePath(input.DestRelativePath);
            mapped.Add(new HeadlessInput { DocsRef = input.DocsRef, DestRelativePath = dest });
        }

        return mapped;
    }

    // EX.7 path-traversal guard. The dest must be a normalised relative
    // path that stays under /workspace/.andy/inputs/. We reject — rather
    // than sanitise — so an upstream bug surfaces as a clear run-start
    // error instead of silently relocating a file. Mirrors the defensive
    // posture FilesystemOutputArtifactCollector takes on the output side
    // (paths-outside-root are skipped).
    public static string ValidateDestRelativePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException(
                "RunInput.DestRelativePath is required (the staging destination under /workspace/.andy/inputs/).",
                nameof(raw));
        }

        // Normalise separators to forward slashes; the inputs root is a
        // POSIX path inside the container.
        var normalised = raw.Replace('\\', '/').Trim();

        if (normalised.StartsWith('/'))
        {
            throw new ArgumentException(
                $"RunInput.DestRelativePath '{raw}' must be relative — no leading slash.", nameof(raw));
        }

        // Reject Windows drive / UNC prefixes that survive separator
        // normalisation (e.g. "C:/x", "//host/share").
        if (normalised.Length >= 2 && normalised[1] == ':')
        {
            throw new ArgumentException(
                $"RunInput.DestRelativePath '{raw}' must be relative — no drive prefix.", nameof(raw));
        }

        var segments = normalised.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new ArgumentException(
                $"RunInput.DestRelativePath '{raw}' resolves to an empty path.", nameof(raw));
        }

        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                throw new ArgumentException(
                    $"RunInput.DestRelativePath '{raw}' must not contain a '..' traversal segment.", nameof(raw));
            }
            if (segment == ".")
            {
                // A bare "." segment is meaningless noise; reject so the
                // staged path is unambiguous.
                throw new ArgumentException(
                    $"RunInput.DestRelativePath '{raw}' must not contain a '.' segment.", nameof(raw));
            }
        }

        // Re-join from the validated segments so the stager works against
        // a canonical, collapsed relative path.
        return string.Join('/', segments);
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
