// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Andy.Containers.Models;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Infrastructure.Audit;

/// <summary>
/// rivoli-ai/andy-containers#320. HTTP client for andy-docs's
/// <c>POST /api/documents:put</c> multipart endpoint (AJ5). Used by
/// <see cref="Andy.Containers.Api.Services.FilesystemOutputArtifactCollector"/>
/// to push the bytes of each <c>RunOutputArtifact</c> into the andy-docs
/// content-addressed store at terminal-event time.
///
/// <para>
/// <strong>Best-effort contract:</strong> every failure mode
/// (network error, 4xx/5xx, timeout, mis-shaped body) surfaces as
/// <c>null</c> with a logged warning — container stop is never blocked
/// on andy-docs availability. Callers that need to distinguish
/// "metadata-only" from "fully uploaded" do so by checking
/// <c>RunOutputArtifact.DocsRef</c> for null.
/// </para>
///
/// <para>
/// Wire contract is identical to andy-tasks's <c>AndyDocsHttpUploader</c>
/// (multipart body with <c>file</c> + <c>meta</c> parts; <c>meta</c> is a
/// JSON document carrying <c>name</c> and <c>links</c>). We deliberately
/// do NOT take a code dependency on andy-tasks — the contract is
/// duplicated here so the two services stay decoupled at the build
/// graph.
/// </para>
///
/// <para>
/// The auth header (when configured) is attached by the named
/// <see cref="HttpClient"/>'s bearer handler — registered in
/// <c>Program.cs</c> via <c>AddBearerFromService</c>. In bypass mode
/// (no <c>AndyAuth:Authority</c>) the client is anonymous; this mirrors
/// the rest of the inbound JWT-bearer / outbound M2M wiring elsewhere
/// in the service.
/// </para>
/// </summary>
public sealed class AndyDocsHttpClient : IAndyDocsClient
{
    /// <summary>Named HttpClient registered in DI; tests can override.</summary>
    public const string HttpClientName = "andy-docs-artifacts";

    // andy-docs uses ASP.NET's default camelCase web JSON options for
    // its response bodies. We pin a dedicated options object so the
    // deserialiser doesn't get cross-contaminated by any process-wide
    // snake-case defaults.
    private static readonly JsonSerializerOptions DocsResponseJson =
        new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AndyDocsHttpClient> _logger;

    public AndyDocsHttpClient(
        IHttpClientFactory httpClientFactory,
        ILogger<AndyDocsHttpClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<DocsRef?> UploadAsync(UploadRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var http = _httpClientFactory.CreateClient(HttpClientName);

        using var content = new MultipartFormDataContent();

        // Materialise the ReadOnlyMemory<byte> into a ByteArrayContent.
        // andy-docs streams the request to disk; the in-memory copy is
        // OK at our artifact size cap.
        var fileContent = new ByteArrayContent(request.Content.ToArray());
        if (!string.IsNullOrWhiteSpace(request.MimeType))
        {
            try
            {
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(request.MimeType);
            }
            catch (FormatException)
            {
                // Defensive: ContentGuessing in the collector should
                // always produce a valid MIME shape, but if a caller
                // hand-rolls a request with a junk value we just drop
                // the header rather than throw — andy-docs falls back
                // to application/octet-stream when missing.
            }
        }
        content.Add(fileContent, name: "file", fileName: SafeFileName(request.Name));

        var meta = BuildMeta(request);
        var metaContent = new StringContent(meta, Encoding.UTF8, "application/json");
        content.Add(metaContent, "meta");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/documents:put")
        {
            Content = content,
        };

        HttpResponseMessage? response = null;
        try
        {
            response = await http.SendAsync(httpRequest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancelled — propagate so the surrounding terminal
            // path can decide what to do (typically skip the event).
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "andy-docs upload failed: network error for digest {Digest} ({Name}).",
                request.Digest, request.Name);
            return null;
        }
        catch (TaskCanceledException ex)
        {
            // Per-attempt HttpClient timeout fired (caller token is NOT
            // cancelled — that path is handled above). Best-effort:
            // metadata-only fallback.
            _logger.LogWarning(ex,
                "andy-docs upload timed out for digest {Digest} ({Name}).",
                request.Digest, request.Name);
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(response.Content, ct).ConfigureAwait(false);
                _logger.LogWarning(
                    "andy-docs upload returned HTTP {Status} ({StatusName}) for digest {Digest} ({Name}). Body preview: {Body}",
                    (int)response.StatusCode, response.StatusCode, request.Digest, request.Name,
                    Truncate(body, 200));
                return null;
            }

            DocsRefWireDto? dto;
            try
            {
                dto = await response.Content
                    .ReadFromJsonAsync<DocsRefWireDto>(DocsResponseJson, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "andy-docs upload succeeded with HTTP {Status} but the response body was unparseable for digest {Digest} ({Name}).",
                    (int)response.StatusCode, request.Digest, request.Name);
                return null;
            }

            if (dto is null || dto.DocumentId == Guid.Empty || dto.LinkId == Guid.Empty)
            {
                _logger.LogWarning(
                    "andy-docs upload returned an incomplete DocsRef for digest {Digest} ({Name}) — documentId or linkId missing.",
                    request.Digest, request.Name);
                return null;
            }

            return new DocsRef(dto.DocumentId, dto.LinkId);
        }
    }

    public async Task<DocumentDownloadResult> DownloadAsync(
        Guid documentId, long maxSizeBytes, CancellationToken ct = default)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("documentId must be a non-empty document id.", nameof(documentId));
        }
        if (maxSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSizeBytes), maxSizeBytes,
                "maxSizeBytes must be positive.");
        }

        var http = _httpClientFactory.CreateClient(HttpClientName);

        // Step 1: resolve the document's head content-hash. A 404 here is
        // a genuine "no such document" — the caller maps it to a clear
        // run-start error. Any other non-success / mis-shaped body is a
        // transient fetch failure.
        DocumentMetaDto? meta;
        try
        {
            using var metaResponse = await http
                .GetAsync($"api/documents/{documentId}", ct)
                .ConfigureAwait(false);

            if (metaResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("andy-docs download: document {DocumentId} not found (404).", documentId);
                return DocumentDownloadResult.Fail(DocumentDownloadFailure.NotFound);
            }
            if (!metaResponse.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(metaResponse.Content, ct).ConfigureAwait(false);
                _logger.LogWarning(
                    "andy-docs download: metadata fetch for {DocumentId} returned HTTP {Status}. Body preview: {Body}",
                    documentId, (int)metaResponse.StatusCode, Truncate(body, 200));
                return DocumentDownloadResult.Fail(DocumentDownloadFailure.FetchFailed);
            }

            meta = await metaResponse.Content
                .ReadFromJsonAsync<DocumentMetaDto>(DocsResponseJson, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex,
                "andy-docs download: metadata fetch for {DocumentId} failed: {Message}", documentId, ex.Message);
            return DocumentDownloadResult.Fail(DocumentDownloadFailure.FetchFailed);
        }

        if (meta is null || string.IsNullOrWhiteSpace(meta.ContentHash))
        {
            _logger.LogWarning(
                "andy-docs download: document {DocumentId} has no head content-hash (empty document?).", documentId);
            return DocumentDownloadResult.Fail(DocumentDownloadFailure.FetchFailed);
        }

        // Step 2: fetch the blob for the resolved head hash. Stream so we
        // can enforce the size cap without materialising an oversized
        // payload into memory.
        try
        {
            using var blobResponse = await http
                .GetAsync($"api/documents/{documentId}/at/{Uri.EscapeDataString(meta.ContentHash)}:blob",
                    HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (blobResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning(
                    "andy-docs download: blob for {DocumentId}@{Hash} not found (404).", documentId, meta.ContentHash);
                return DocumentDownloadResult.Fail(DocumentDownloadFailure.NotFound);
            }
            if (!blobResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "andy-docs download: blob fetch for {DocumentId}@{Hash} returned HTTP {Status}.",
                    documentId, meta.ContentHash, (int)blobResponse.StatusCode);
                return DocumentDownloadResult.Fail(DocumentDownloadFailure.FetchFailed);
            }

            // Reject up-front if the server advertises an oversized
            // Content-Length, before reading a single byte.
            var declaredLength = blobResponse.Content.Headers.ContentLength;
            if (declaredLength is { } len && len > maxSizeBytes)
            {
                _logger.LogWarning(
                    "andy-docs download: blob for {DocumentId} is {Length} bytes — exceeds input cap ({Max}).",
                    documentId, len, maxSizeBytes);
                return DocumentDownloadResult.Fail(DocumentDownloadFailure.TooLarge);
            }

            var mimeType = blobResponse.Content.Headers.ContentType?.MediaType;

            await using var stream = await blobResponse.Content
                .ReadAsStreamAsync(ct).ConfigureAwait(false);
            var bytes = await ReadCappedAsync(stream, maxSizeBytes, ct).ConfigureAwait(false);
            if (bytes is null)
            {
                _logger.LogWarning(
                    "andy-docs download: blob for {DocumentId} exceeded input cap ({Max}) while streaming.",
                    documentId, maxSizeBytes);
                return DocumentDownloadResult.Fail(DocumentDownloadFailure.TooLarge);
            }

            return DocumentDownloadResult.Ok(bytes.Value, mimeType);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger.LogWarning(ex,
                "andy-docs download: blob fetch for {DocumentId} failed: {Message}", documentId, ex.Message);
            return DocumentDownloadResult.Fail(DocumentDownloadFailure.FetchFailed);
        }
    }

    // Read at most maxSizeBytes+1 from the stream. Returns null the moment
    // we read past the cap (so the caller maps to TooLarge rather than
    // OOMing on an unbounded body the Content-Length header lied about).
    private static async Task<ReadOnlyMemory<byte>?> ReadCappedAsync(
        Stream stream, long maxSizeBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxSizeBytes)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // The `meta` part is a JSON document carrying the upload's name and
    // links. Schema mirrors andy-tasks's BuildMeta — keep these in sync
    // by hand; both target the same andy-docs endpoint.
    private static string BuildMeta(UploadRequest request)
    {
        var meta = new
        {
            name = request.Name,
            links = request.Links.Select(l => new
            {
                targetType = l.TargetType,
                targetId = l.TargetId,
                role = l.Role,
            }).ToArray(),
        };
        return JsonSerializer.Serialize(meta);
    }

    // Strip control chars from the filename used in the multipart
    // Content-Disposition. RFC 7578 lets the server reject filenames
    // containing CR/LF; we sanitise defensively so the response is
    // not lost to a 400.
    private static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "artifact";
        Span<char> buf = stackalloc char[Math.Min(name.Length, 256)];
        var n = 0;
        foreach (var c in name)
        {
            if (n >= buf.Length) break;
            if (c == '\r' || c == '\n' || c == '"') continue;
            buf[n++] = c;
        }
        return n == 0 ? "artifact" : new string(buf[..n]);
    }

    private static async Task<string> SafeReadAsync(HttpContent content, CancellationToken ct)
    {
        try
        {
            return await content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max] + "...";
    }

    /// <summary>
    /// Wire shape returned by andy-docs's <c>POST /api/documents:put</c>.
    /// Mirrors the relevant fields on <c>DocsRefDto</c> — additional
    /// fields (versionHash, sizeBytes, additionalLinkIds) are present on
    /// the wire but unused here so the local record stays minimal.
    /// </summary>
    private sealed record DocsRefWireDto(
        Guid DocumentId,
        Guid LinkId);

    /// <summary>
    /// EX.7 (rivoli-ai/andy-containers#328). Subset of andy-docs's
    /// <c>DocumentDto</c> (<c>GET /api/documents/{id}</c>) we need to
    /// resolve a document's head blob: <c>contentHash</c> identifies the
    /// current version whose bytes the <c>:blob</c> endpoint serves. Other
    /// fields (name, title, content, parentFolderId, createdAt) are present
    /// on the wire but unused here.
    /// </summary>
    private sealed record DocumentMetaDto(string? ContentHash);
}
