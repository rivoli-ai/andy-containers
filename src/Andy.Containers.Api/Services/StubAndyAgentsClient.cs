using Andy.Containers.Configurator;

namespace Andy.Containers.Api.Services;

/// <summary>
/// AP3 (rivoli-ai/andy-containers#105) placeholder client. The real
/// andy-agents service does not exist yet (Epic W); this stub returns
/// reasonable fixture specs keyed off the agent slug so AP3 + AP6 can
/// run end-to-end. Replace with an HTTP client once andy-agents is up.
/// </summary>
/// <remarks>
/// TODO(andy-agents): Swap for an HTTP-backed implementation that calls
/// <c>GET /api/agents/{slug}?revision=N</c> on the andy-agents service.
/// Fixture data here intentionally tracks the AQ1 sample configs so the
/// configurator output stays close to the canonical shapes during local dev.
/// </remarks>
public sealed class StubAndyAgentsClient : IAndyAgentsClient
{
    private static readonly Dictionary<string, AgentSpec> Fixtures = new(StringComparer.Ordinal)
    {
        ["triage-agent"] = new AgentSpec
        {
            Slug = "triage-agent",
            Revision = 1,
            Instructions = "You are the triage agent. Classify incoming issues against the Rivoli template set.",
            OutputFormat = "json-triage-output-v1",
            Model = new AgentSpecModel
            {
                Provider = "anthropic",
                Id = "claude-sonnet-4-6",
                ApiKeyRef = "env:ANDY_MODEL_KEY",
            },
            Tools = new[]
            {
                new AgentSpecTool { Name = "issues.get", Transport = "mcp", Endpoint = "https://mcp.internal/tools/issues.get" },
            },
            Boundaries = new[] { "read-only" },
            Limits = new AgentSpecLimits { MaxIterations = 50, TimeoutSeconds = 300 },
        },
        ["planning-agent"] = new AgentSpec
        {
            Slug = "planning-agent",
            Instructions = "You are the planning agent. Decompose the triaged issue into TaskNodes.",
            OutputFormat = "json-plan-v1",
            Model = new AgentSpecModel
            {
                Provider = "anthropic",
                Id = "claude-opus-4-7",
                ApiKeyRef = "env:ANDY_MODEL_KEY",
            },
            Tools = new[]
            {
                new AgentSpecTool { Name = "docs.put", Transport = "mcp", Endpoint = "https://mcp.internal/tools/docs.put" },
            },
            Boundaries = new[] { "draft-only" },
            Limits = new AgentSpecLimits { MaxIterations = 120, TimeoutSeconds = 900 },
        },
        ["coding-agent"] = new AgentSpec
        {
            Slug = "coding-agent",
            Instructions = "You are the coding agent. Implement the assigned TaskNode against the delegation contract.",
            OutputFormat = "plain",
            Model = new AgentSpecModel
            {
                Provider = "anthropic",
                Id = "claude-sonnet-4-6",
                ApiKeyRef = "env:ANDY_MODEL_KEY",
            },
            Tools = new[]
            {
                new AgentSpecTool { Name = "fs.patch", Transport = "mcp", Endpoint = "https://mcp.internal/tools/fs.patch" },
                new AgentSpecTool
                {
                    Name = "git",
                    Transport = "cli",
                    Binary = "git",
                    Command = new[] { "git" },
                },
            },
            Boundaries = new[] { "write-branch", "sandboxed" },
            Limits = new AgentSpecLimits { MaxIterations = 400, TimeoutSeconds = 3600 },
        },
    };

    public Task<AgentSpec?> GetAgentAsync(string agentSlug, int? revision, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentSlug))
        {
            return Task.FromResult<AgentSpec?>(null);
        }

        if (!Fixtures.TryGetValue(agentSlug, out var spec))
        {
            return Task.FromResult<AgentSpec?>(null);
        }

        // Revision pinning is best-effort against the stub: if the caller
        // asks for a specific revision, echo it back rather than the
        // fixture's default so AP6 sees the pin propagated end-to-end.
        if (revision.HasValue)
        {
            spec = spec with { Revision = revision };
        }

        return Task.FromResult<AgentSpec?>(spec);
    }
}
