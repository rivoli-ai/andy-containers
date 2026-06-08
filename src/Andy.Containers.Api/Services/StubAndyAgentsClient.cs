using Andy.Containers.Configurator;

namespace Andy.Containers.Api.Services;

/// <summary>
/// AP3 (rivoli-ai/andy-containers#105) placeholder client. The real
/// andy-agents-backed resolver is Epic W; until it lands this stub
/// synthesises a runnable <see cref="AgentSpec"/> for the agent slugs the
/// andy-tasks planner actually emits (the andy-agents roster: <c>coding</c>,
/// <c>review</c>, <c>planning</c>, <c>triage</c>, <c>research</c>,
/// <c>validation</c>), plus the legacy <c>*-agent</c> aliases.
/// </summary>
/// <remarks>
/// The synthesised model routes through Conductor's embedded andy-models
/// proxy, NOT a public provider endpoint: the in-container andy-cli reaches
/// the proxy via <c>ANDY_PROXY_BASE_URL</c> (host.docker.internal:9100/models)
/// using the per-container <c>ANDY_SERVICE_TOKEN</c> as its bearer. So the
/// spec declares the OpenAI-compatible dialect (<c>provider = "openai"</c> —
/// the proxy speaks it, and it's in HeadlessConfigBuilder's allow-list, unlike
/// the raw <c>"openrouter"</c> the old fixtures used) and the model slug
/// registered in andy-models (<c>deepseek-v4-flash</c>, which the proxy maps
/// to OpenRouter upstream). The api-key ref points at the injected service
/// token rather than a provider key the container doesn't hold.
///
/// TODO(andy-agents / Epic W): replace with an HTTP client that resolves the
/// agent (instructions, model, tools, limits) from andy-agents
/// <c>GET /api/agents/by-slug/{slug}</c>.
/// </remarks>
public sealed class StubAndyAgentsClient : IAndyAgentsClient
{
    // Model the in-container agent uses: the OpenAI-compatible dialect spoken
    // by the andy-models proxy + the registered deepseek-v4-flash slug + the
    // injected per-container service token.
    private static AgentSpecModel ProxyModel() => new()
    {
        Provider = "openai",
        Id = "deepseek-v4-flash",
        ApiKeyRef = "env:ANDY_SERVICE_TOKEN",
    };

    // Per-role presentation. Keyed by the andy-agents slug the planner emits;
    // the legacy "<role>-agent" aliases resolve to the same entry.
    private static readonly Dictionary<string, (string Instructions, string OutputFormat, string[] Boundaries, int MaxIter, int Timeout)> Roles =
        new(StringComparer.Ordinal)
        {
            ["triage"] = ("You are the triage agent. Classify the incoming issue against the Rivoli template set.",
                "json-triage-output-v1", new[] { "read-only" }, 50, 300),
            ["planning"] = ("You are the planning agent. Decompose the triaged issue into TaskNodes.",
                "json-plan-v1", new[] { "draft-only" }, 120, 900),
            ["research"] = ("You are the research agent. Gather the context the plan needs and summarise it.",
                "plain", new[] { "read-only" }, 120, 900),
            ["coding"] = ("You are the coding agent. Implement the assigned TaskNode against the delegation contract, editing files in /workspace and leaving the change on a branch for human review.",
                "plain", new[] { "write-branch", "sandboxed" }, 400, 3600),
            ["review"] = ("You are the review agent. Inspect the diff produced by the coding task for safety and correctness; do not modify files.",
                "plain", new[] { "read-only" }, 200, 1800),
            ["validation"] = ("You are the validation agent. Run the declared verifier and report pass/fail.",
                "plain", new[] { "read-only" }, 200, 1800),
        };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["triage-agent"] = "triage",
        ["planning-agent"] = "planning",
        ["coding-agent"] = "coding",
        ["review-agent"] = "review",
        ["research-agent"] = "research",
        ["validation-agent"] = "validation",
    };

    public Task<AgentSpec?> GetAgentAsync(string agentSlug, int? revision, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentSlug))
        {
            return Task.FromResult<AgentSpec?>(null);
        }

        var key = Aliases.TryGetValue(agentSlug, out var canonical) ? canonical : agentSlug;
        if (!Roles.TryGetValue(key, out var role))
        {
            return Task.FromResult<AgentSpec?>(null);
        }

        // The coding role gets the local `git` CLI for branch/diff work;
        // file editing uses andy-cli's BUILT-IN tools (no external MCP server).
        // Everyone else is read-only with no tools. (The previous fs.patch MCP
        // tool pointed at a placeholder `mcp.internal` host that doesn't
        // resolve, which crashed the in-container agent on startup.)
        var tools = key == "coding"
            ? new[]
            {
                new AgentSpecTool { Name = "git", Transport = "cli", Binary = "git", Command = new[] { "git" } },
            }
            : Array.Empty<AgentSpecTool>();

        var spec = new AgentSpec
        {
            Slug = agentSlug,
            Revision = revision ?? 1,
            Instructions = role.Instructions,
            OutputFormat = role.OutputFormat,
            Model = ProxyModel(),
            Tools = tools,
            Boundaries = role.Boundaries,
            Limits = new AgentSpecLimits { MaxIterations = role.MaxIter, TimeoutSeconds = role.Timeout },
        };

        return Task.FromResult<AgentSpec?>(spec);
    }
}
