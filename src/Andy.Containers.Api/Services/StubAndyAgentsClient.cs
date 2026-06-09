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
            ["coding"] = ("You are the coding agent working inside a sandboxed container. Your repository is checked out at /workspace (the current working directory). Implement the task described below by EDITING FILES — do not ask the user for clarification; you already have everything you need, so act. Use the `shell` tool to inspect and modify files: pass a single bash command string as the args element, e.g. `cat README.md`, `ls`, or a heredoc/printf/sed to write changes (the command runs as `bash -c \"<your string>\"` in /workspace). Use `git` for diff/branch/commit. Make the minimal change that satisfies the task, verify it by reading the file back, then stop. Keep your edits on the working tree for human review.",
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

        // The coding role gets a `shell` tool (the agent's actual file-editing
        // capability) plus the local `git` CLI for branch/diff work. Everyone
        // else is read-only with no tools.
        //
        // andy-cli headless registers NO built-in file tools — the agent's
        // entire tool surface is exactly what's declared here (HeadlessToolHost
        // only wires `cli`/`mcp` transports). So a coding agent with only `git`
        // literally cannot write files; it needs an exec capability. `shell`
        // maps to `bash -c <script>` via CliSubprocessTool (the LLM supplies the
        // script as a single `args` element), which is how andy-cli edits files
        // in the sandboxed container. (The earlier fs.patch MCP tool pointed at
        // a placeholder `mcp.internal` host that didn't resolve and crashed the
        // agent on startup; a shell tool needs no external server.)
        var tools = key == "coding"
            ? new[]
            {
                new AgentSpecTool
                {
                    Name = "shell",
                    Transport = "cli",
                    Binary = "bash",
                    Command = new[] { "bash", "-c" },
                },
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
