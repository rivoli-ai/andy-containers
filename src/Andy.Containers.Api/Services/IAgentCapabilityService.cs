namespace Andy.Containers.Api.Services;

/// <summary>
/// X9 (rivoli-ai/andy-containers#99). Resolves an agent's
/// <c>allowed_environments</c> declaration so the workspace-create
/// surface can enforce that a workspace's bound EnvironmentProfile is
/// permitted by the agent the workspace targets.
/// </summary>
/// <remarks>
/// Today an in-process stub returns <c>null</c> ("no allowlist on
/// record — allow all"); when andy-agents (Epic W3) ships
/// <c>GET /api/agents/{id}/allowed-environments</c>, swap the
/// implementation for the HTTP client without touching callers.
///
/// The contract is deliberately minimal: caller passes the agent id,
/// gets the list of profile codes the agent permits, or null to
/// signal "no policy on this agent". A 403 only fires when an
/// agent has an explicit allowlist that doesn't include the
/// workspace's profile — agents without a policy stay open.
/// </remarks>
public interface IAgentCapabilityService
{
    /// <summary>
    /// Fetch the agent's allowed environment-profile codes.
    /// </summary>
    /// <returns>
    /// Non-null = the explicit allowlist (case-insensitive match).
    /// Null = no policy on record; treat as "all allowed".
    /// </returns>
    /// <exception cref="AgentCapabilityServiceUnavailableException">
    /// Thrown when the upstream agents service is reachable-but-erroring
    /// (5xx, transport failure). Callers fail-closed: the workspace-
    /// create flow returns 503 rather than provisioning a workspace
    /// against an unverifiable policy.
    /// </exception>
    Task<IReadOnlyList<string>?> GetAllowedEnvironmentsAsync(string agentId, CancellationToken ct = default);
}

/// <summary>
/// Raised when the agents service can't answer a capability query.
/// Distinct from "agent has no allowlist" (null result) so the
/// fail-closed branch is explicit at the caller.
/// </summary>
public sealed class AgentCapabilityServiceUnavailableException : Exception
{
    public AgentCapabilityServiceUnavailableException(string message)
        : base(message)
    {
    }

    public AgentCapabilityServiceUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// X9 in-process stub. Always returns null (= no policy on record).
/// Replace with the andy-agents HTTP client when W3 lands.
/// </summary>
public sealed class StubAgentCapabilityService : IAgentCapabilityService
{
    public Task<IReadOnlyList<string>?> GetAllowedEnvironmentsAsync(
        string agentId, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<string>?>(null);
    }
}
