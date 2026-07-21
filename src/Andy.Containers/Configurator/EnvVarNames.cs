namespace Andy.Containers.Configurator;

/// <summary>
/// AP10 (rivoli-ai/andy-containers#112). Canonical names for the env
/// vars the trusted container launcher injects into the andy-cli process
/// environment so the agent can call back to the platform.
/// Centralised so config-builder, runner, and tests refer to the same
/// strings — drift between layers is the bug class this constant
/// avoids.
/// </summary>
public static class EnvVarNames
{
    /// <summary>Run-scoped bearer token minted by <see cref="ITokenIssuer"/>.</summary>
    public const string AndyToken = "ANDY_TOKEN";

    /// <summary>Base URL of andy-proxy (egress mediator).</summary>
    public const string AndyProxyUrl = "ANDY_PROXY_URL";

    /// <summary>Base URL of the platform's MCP server.</summary>
    public const string AndyMcpUrl = "ANDY_MCP_URL";

    private static readonly HashSet<string> RuntimeIdentityNames = new(StringComparer.Ordinal)
    {
        AndyToken,
        AndyProxyUrl,
        AndyMcpUrl,
    };

    /// <summary>
    /// True when <paramref name="name"/> is owned by the trusted container
    /// runtime and must never appear in headless config <c>env_vars</c>.
    /// </summary>
    public static bool IsRuntimeIdentity(string name) => RuntimeIdentityNames.Contains(name);
}
