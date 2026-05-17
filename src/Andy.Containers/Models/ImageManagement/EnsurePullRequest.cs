namespace Andy.Containers.Models.ImageManagement;

/// <summary>
/// Request body for <c>POST /api/images/ensure-pull</c>.
/// rivoli-ai/conductor#1014 — Conductor uses this to seed required
/// terminal images into the local registry from an upstream public
/// registry (e.g. <c>ghcr.io/rivoli-ai</c>).
///
/// Semantics are idempotent: if the destination already holds an
/// artifact at <see cref="DestinationRepository"/>:<see cref="DestinationTag"/>,
/// the endpoint returns 200 with the existing artifact instead of
/// re-pulling. Conductor relies on this so a poll loop can call
/// the endpoint cheaply on every tick without re-doing the work.
/// </summary>
public sealed class EnsurePullRequest
{
    /// <summary>
    /// Upstream registry host, e.g. <c>ghcr.io</c> or
    /// <c>registry.rivoli-ai.com</c>. Required.
    /// </summary>
    public required string SourceRegistry { get; init; }

    /// <summary>
    /// Upstream repository path, e.g. <c>rivoli-ai/conductor-terminal-claude-code</c>.
    /// Required.
    /// </summary>
    public required string SourceRepository { get; init; }

    /// <summary>
    /// Upstream tag, e.g. <c>v1</c> or <c>latest</c>. Required.
    /// </summary>
    public required string SourceTag { get; init; }

    /// <summary>
    /// Identifier of the destination registry (must match a
    /// <c>RegistryConfigEntry.Id</c> the host knows about, e.g.
    /// <c>local-zot</c>).
    /// </summary>
    public required string DestinationRegistryId { get; init; }

    /// <summary>
    /// Repository path under the destination. Defaults to
    /// <see cref="SourceRepository"/>'s last path segment when null
    /// — the standard re-host shape Conductor uses.
    /// </summary>
    public string? DestinationRepository { get; init; }

    /// <summary>
    /// Tag under the destination. Defaults to <see cref="SourceTag"/>
    /// when null.
    /// </summary>
    public string? DestinationTag { get; init; }
}
