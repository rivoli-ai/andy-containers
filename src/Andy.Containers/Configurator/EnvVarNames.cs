namespace Andy.Containers.Configurator;

/// <summary>
/// AP10 (rivoli-ai/andy-containers#112). Canonical names for the env
/// vars the configurator injects into a run's environment so the
/// agent process inside andy-cli can call back to the platform.
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
}
