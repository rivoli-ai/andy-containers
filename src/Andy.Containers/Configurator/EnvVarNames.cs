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

    /// <summary>
    /// Per-run token attribution (rivoli-ai/conductor#1947). The headless
    /// agent reads these and stamps them as the X-Andy-Run-Id /
    /// X-Andy-Task-Id / X-Andy-Agent-Id headers on every andy-models call
    /// so andy-models can attribute token usage + cost to the run / task /
    /// agent. The runner owns this identity (it's on the <c>Run</c>); the
    /// agent is just the propagation carrier. Task is the run's
    /// correlation id (the andy-tasks task/goal it belongs to), omitted
    /// when the run carries none.
    /// </summary>
    public const string AndyRunId = "ANDY_RUN_ID";

    /// <inheritdoc cref="AndyRunId"/>
    public const string AndyTaskId = "ANDY_TASK_ID";

    /// <inheritdoc cref="AndyRunId"/>
    public const string AndyAgentId = "ANDY_AGENT_ID";
}
