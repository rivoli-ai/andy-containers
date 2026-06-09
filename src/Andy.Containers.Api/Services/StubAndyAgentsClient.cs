using Andy.Containers.Configurator;

namespace Andy.Containers.Api.Services;

/// <summary>
/// AP3 (rivoli-ai/andy-containers#105) in-process fallback client. As of AX.1
/// (rivoli-ai/conductor#2088) the real andy-agents-backed resolver is
/// <see cref="AndyAgentsHttpClient"/> — the default whenever
/// <c>AndyAgents:ApiBaseUrl</c> is configured. This stub remains the fallback
/// for dev / embedded mode where no andy-agents instance is reachable, and
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
/// AX.1 done: <see cref="AndyAgentsHttpClient"/> resolves the agent
/// (instructions + model) from andy-agents <c>GET /api/agents/by-slug/{slug}</c>.
/// Tools are NOT sourced there — they're built into the in-container assistant
/// (andy-cli AX.3) and gated by the injected permission allow-list (andy-cli
/// AX.4); the real spec's Tools is always EMPTY. AX.5 brings this stub in line:
/// it no longer synthesises a <c>shell</c>/<c>git</c> tool for the coding role,
/// and its coding prompt no longer names a tool transport — the agent uses its
/// built-in file/edit tools, not a synthesised cli tool.
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
        // The in-container OpenAI-dialect client reads its bearer from
        // OPENAI_API_KEY — andy-containers injects a per-container proxy token
        // there (aud=urn:andy-models-api) alongside OPENAI_BASE_URL pointed at
        // the andy-models proxy. (The shared ANDY_SERVICE_TOKEN is
        // aud=urn:andy-containers-api and the proxy rejects it 401, so the key
        // ref must name OPENAI_API_KEY, not ANDY_SERVICE_TOKEN.)
        ApiKeyRef = "env:OPENAI_API_KEY",
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
            ["coding"] = ("You are the coding agent working inside a sandboxed container. Your repository is checked out at /workspace (the current working directory). Implement the task described below by editing files — do not ask the user for clarification; you already have everything you need, so act. Make the minimal change that satisfies the task, verify it by reading the changed files back, then stop. Leave your edits on the working tree for human review.",
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

        // Tools are EMPTY for every role, mirroring AndyAgentsHttpClient. Under
        // the corrected tools model the assistant's tools are BUILT-IN (andy-cli
        // AX.3) and gated by the injected permission allow-list (andy-cli AX.4):
        // andy-agents (and this fallback) no longer synthesise cli tools. The
        // coding role used to carry a `shell`(bash -c)+`git` spec here; AX.5
        // retires that interim stopgap.
        var spec = new AgentSpec
        {
            Slug = agentSlug,
            Revision = revision ?? 1,
            Instructions = role.Instructions,
            OutputFormat = role.OutputFormat,
            Model = ProxyModel(),
            Tools = Array.Empty<AgentSpecTool>(),
            Boundaries = role.Boundaries,
            Limits = new AgentSpecLimits { MaxIterations = role.MaxIter, TimeoutSeconds = role.Timeout },
        };

        return Task.FromResult<AgentSpec?>(spec);
    }
}
