namespace Andy.Containers.Configurator;

/// <summary>
/// AP3 (rivoli-ai/andy-containers#105) view of the resolved agent spec the
/// configurator consumes. The shape mirrors what andy-agents will eventually
/// return over HTTP — once that service exists, swap the
/// <see cref="IAndyAgentsClient"/> implementation but keep this type stable.
/// </summary>
/// <remarks>
/// "Resolved" means skills have already been expanded to concrete tool
/// bindings; the configurator does not re-walk skill graphs. Boundaries and
/// limits come from the policy engine + agent metadata server-side.
/// </remarks>
public sealed record AgentSpec
{
    public required string Slug { get; init; }
    public int? Revision { get; init; }
    public required string Instructions { get; init; }
    public string? OutputFormat { get; init; }
    public required AgentSpecModel Model { get; init; }
    public IReadOnlyList<AgentSpecTool> Tools { get; init; } = [];
    public IReadOnlyDictionary<string, string>? EnvVars { get; init; }
    public IReadOnlyList<string>? Boundaries { get; init; }
    public AgentSpecLimits Limits { get; init; } = new();
}

public sealed record AgentSpecModel
{
    public required string Provider { get; init; }
    public required string Id { get; init; }
    public string? ApiKeyRef { get; init; }
}

/// <summary>
/// Discriminated by <see cref="Transport"/> ("mcp" or "cli"). MCP bindings set
/// <see cref="Endpoint"/>; CLI bindings set <see cref="Binary"/> (and optionally
/// <see cref="Command"/>). The headless schema enforces the oneOf shape — the
/// builder validates the same invariant before emitting the config.
/// </summary>
public sealed record AgentSpecTool
{
    public required string Name { get; init; }
    public required string Transport { get; init; }
    public string? Endpoint { get; init; }
    public string? Binary { get; init; }
    public IReadOnlyList<string>? Command { get; init; }
}

public sealed record AgentSpecLimits
{
    public int MaxIterations { get; init; } = 100;
    public int TimeoutSeconds { get; init; } = 900;
}
