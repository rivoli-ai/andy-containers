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

    /// <summary>
    /// EX.7 (rivoli-ai/andy-containers#328). Download the current-version
    /// bytes of an andy-docs document by id. Resolves the document's head
    /// content-hash (<c>GET /api/documents/{id}</c>) and fetches that blob
    /// (<c>GET /api/documents/{id}/at/{hash}:blob</c>).
    ///
    /// <para>
    /// Unlike <see cref="UploadAsync"/> (best-effort, null-on-failure),
    /// download is on the run-START critical path: a missing or failed
    /// fetch MUST fail the run rather than start the agent with an empty
    /// input. The return type therefore distinguishes the failure modes so
    /// the caller can map each to a clear, typed run-start error:
    /// <see cref="DocumentDownloadResult"/> carries either the bytes or a
    /// <see cref="DocumentDownloadFailure"/>. The method throws only on
    /// caller-initiated cancellation or programmer error.
    /// </para>
    /// </summary>
    /// <param name="maxSizeBytes">
    /// Hard cap on the downloaded payload. A document whose declared or
    /// streamed size exceeds this returns
    /// <see cref="DocumentDownloadFailure.TooLarge"/>.
    /// </param>
    Task<DocumentDownloadResult> DownloadAsync(
        Guid documentId, long maxSizeBytes, CancellationToken ct = default);
}

/// <summary>
/// EX.7 download outcome. Exactly one of <see cref="Content"/> /
/// <see cref="Failure"/> is set: <see cref="Failure"/> is
/// <see cref="DocumentDownloadFailure.None"/> on success.
/// </summary>
public sealed record DocumentDownloadResult(
    DocumentDownloadFailure Failure,
    ReadOnlyMemory<byte> Content,
    string? MimeType)
{
    public bool IsSuccess => Failure == DocumentDownloadFailure.None;

    public static DocumentDownloadResult Ok(ReadOnlyMemory<byte> content, string? mimeType) =>
        new(DocumentDownloadFailure.None, content, mimeType);

    public static DocumentDownloadResult Fail(DocumentDownloadFailure failure) =>
        new(failure, ReadOnlyMemory<byte>.Empty, null);
}

/// <summary>EX.7 download failure classes, mapped to typed run-start errors by the stager.</summary>
public enum DocumentDownloadFailure
{
    /// <summary>No failure — the result carries content.</summary>
    None = 0,

    /// <summary>Document id (or its head version) not found in andy-docs (404).</summary>
    NotFound,

    /// <summary>Document exceeds the configured input size cap.</summary>
    TooLarge,

    /// <summary>Transient / unexpected fetch failure (network error, 5xx, timeout, mis-shaped body).</summary>
    FetchFailed,
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
