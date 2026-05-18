// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Models;

namespace Andy.Containers.Infrastructure.Audit;

/// <summary>
/// rivoli-ai/andy-containers#320. Andy-docs upload surface used by
/// the artifact collector to push OutputArtifact bytes to the
/// content-addressed document store at terminal-event time.
///
/// <para>
/// <strong>Failure semantics:</strong> <see cref="UploadAsync"/> returns
/// <c>null</c> on any transient or recoverable failure (network error,
/// 5xx, timeout, mis-shaped response) rather than throwing. This is the
/// "best-effort" contract — container stop must NOT block on andy-docs
/// availability. A null return signals the caller to fall back to a
/// metadata-only artifact entry (no <c>DocsRef</c> populated). Throws
/// only on caller-initiated cancellation or programmer error (null
/// arguments).
/// </para>
///
/// <para>
/// Mirrors the wire contract used by andy-tasks's
/// <c>AndyDocsHttpUploader</c> (AD3) — both target the same
/// <c>POST /api/documents:put</c> multipart endpoint. We deliberately do
/// NOT take a code dependency on andy-tasks; the contract is duplicated
/// locally so the two services stay decoupled at the build-graph level.
/// </para>
/// </summary>
public interface IAndyDocsClient
{
    Task<DocsRef?> UploadAsync(UploadRequest request, CancellationToken ct = default);
}

/// <summary>
/// One upload payload bound for <c>POST /api/documents:put</c>. Content
/// is held in memory rather than streamed because the artifact size cap
/// (see <c>FilesystemOutputArtifactCollector</c>) is well within the
/// per-request budget; switching to streaming requires a sidecar
/// stream-based exec channel that the current
/// <see cref="Andy.Containers.Abstractions.IContainerService"/> surface
/// does not expose.
/// </summary>
public sealed record UploadRequest(
    ReadOnlyMemory<byte> Content,
    string MimeType,
    string Name,
    string Digest,
    IReadOnlyList<DocumentLinkDescriptor> Links);

/// <summary>
/// Single <c>(targetType, targetId, role)</c> link tuple that andy-docs
/// records on the uploaded document. The first link becomes the
/// primary <c>LinkId</c> returned in <see cref="DocsRef"/>; additional
/// links are recorded but not surfaced individually in the return type
/// (andy-docs's wire response also surfaces them under a separate
/// <c>additionalLinkIds</c> field which we intentionally do not project).
/// </summary>
public sealed record DocumentLinkDescriptor(
    string TargetType,
    string TargetId,
    string Role);
