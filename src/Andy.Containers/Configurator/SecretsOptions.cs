namespace Andy.Containers.Configurator;

/// <summary>
/// AP10 (rivoli-ai/andy-containers#112). Per-deployment secrets-scope
/// settings consumed by <see cref="RunConfigurator"/> when it injects
/// the <c>ANDY_PROXY_URL</c> and <c>ANDY_MCP_URL</c> env vars into the
/// agent's environment. <see cref="ITokenIssuer"/> mints
/// <c>ANDY_TOKEN</c> separately.
/// </summary>
/// <remarks>
/// Bound from the <c>Secrets</c> section of <c>appsettings.json</c>:
/// <code>
/// "Secrets": {
///   "ProxyUrl": "https://proxy.andy.local",
///   "McpUrl":   "https://mcp.andy.local"
/// }
/// </code>
/// Defaults are deliberately localhost-shaped — production deployments
/// must override. The configurator skips injecting an env var whose
/// value is null/empty so a half-configured environment surfaces as a
/// missing var rather than a misleading "localhost" injection.
/// </remarks>
public sealed class SecretsOptions
{
    public const string SectionName = "Secrets";

    /// <summary>Base URL of the andy-proxy service (egress mediator).</summary>
    public string? ProxyUrl { get; set; }

    /// <summary>Base URL of the MCP server agents register tools against.</summary>
    public string? McpUrl { get; set; }
}
