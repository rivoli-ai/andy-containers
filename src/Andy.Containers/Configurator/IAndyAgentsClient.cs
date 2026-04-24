namespace Andy.Containers.Configurator;

/// <summary>
/// AP3 (rivoli-ai/andy-containers#105). Abstraction over the andy-agents
/// service — the source of truth for agent specs (slug, instructions, model
/// preference, allowed tools, boundaries, limits).
/// </summary>
/// <remarks>
/// The real andy-agents HTTP service does not exist yet; the in-process
/// <c>StubAndyAgentsClient</c> ships AP3-built fixtures so the configurator
/// can be exercised end-to-end. Swap the implementation once andy-agents
/// is reachable; the interface is intentionally narrow to keep the eventual
/// HTTP client trivial.
/// </remarks>
public interface IAndyAgentsClient
{
    /// <summary>
    /// Resolves the spec for <paramref name="agentSlug"/>, optionally pinned
    /// to <paramref name="revision"/> (null = head). Returns null when the
    /// agent is unknown so callers can map to a 404-equivalent.
    /// </summary>
    Task<AgentSpec?> GetAgentAsync(
        string agentSlug,
        int? revision,
        CancellationToken ct = default);
}
