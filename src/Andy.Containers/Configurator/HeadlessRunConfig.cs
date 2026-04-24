namespace Andy.Containers.Configurator;

/// <summary>
/// C# mirror of the andy-cli AQ1 headless-config schema
/// (<c>schemas/headless-config.v1.json</c> in rivoli-ai/andy-cli). Property
/// names are PascalCase here; the configurator serializer applies
/// <see cref="System.Text.Json.JsonNamingPolicy.SnakeCaseLower"/> so the
/// emitted JSON matches the schema's snake_case wire names without
/// per-property attributes.
/// </summary>
/// <remarks>
/// AP3 (rivoli-ai/andy-containers#105). Re-defining the type here (rather
/// than referencing andy-cli's <c>HeadlessRunConfig</c>) keeps the contract
/// asymmetric in the right direction: andy-cli owns the canonical schema
/// file, andy-containers owns the producer side. A schema bump (v2) means
/// adding a sibling type, not a breaking change to v1 consumers.
/// </remarks>
public sealed record HeadlessRunConfig
{
    public int SchemaVersion { get; init; } = 1;
    public Guid RunId { get; init; }
    public HeadlessAgent Agent { get; init; } = new();
    public HeadlessModel Model { get; init; } = new();
    public IReadOnlyList<HeadlessTool> Tools { get; init; } = [];
    public HeadlessWorkspace Workspace { get; init; } = new();
    public IReadOnlyDictionary<string, string>? EnvVars { get; init; }
    public HeadlessOutput Output { get; init; } = new();
    public HeadlessEventSink? EventSink { get; init; }
    public Guid? PolicyId { get; init; }
    public IReadOnlyList<string>? Boundaries { get; init; }
    public HeadlessLimits Limits { get; init; } = new();
}

public sealed record HeadlessAgent
{
    public string Slug { get; init; } = string.Empty;
    public int? Revision { get; init; }
    public string Instructions { get; init; } = string.Empty;
    public string? OutputFormat { get; init; }
}

public sealed record HeadlessModel
{
    public string Provider { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string? ApiKeyRef { get; init; }
}

public sealed record HeadlessTool
{
    public string Name { get; init; } = string.Empty;
    public string Transport { get; init; } = string.Empty;
    public string? Endpoint { get; init; }
    public string? Binary { get; init; }
    public IReadOnlyList<string>? Command { get; init; }
}

public sealed record HeadlessWorkspace
{
    public string Root { get; init; } = string.Empty;
    public string? Branch { get; init; }
}

public sealed record HeadlessOutput
{
    public string File { get; init; } = string.Empty;
    public string Stream { get; init; } = string.Empty;
}

public sealed record HeadlessEventSink
{
    public string? NatsSubject { get; init; }
    public string? Path { get; init; }
}

public sealed record HeadlessLimits
{
    public int MaxIterations { get; init; }
    public int TimeoutSeconds { get; init; }
}
