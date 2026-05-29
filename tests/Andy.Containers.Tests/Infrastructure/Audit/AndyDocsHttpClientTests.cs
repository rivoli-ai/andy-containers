// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Text;
using System.Text.Json;
using Andy.Containers.Infrastructure.Audit;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Tests.Infrastructure.Audit;

// rivoli-ai/andy-containers#320. HttpMessageHandler-stub tests for the
// andy-docs upload client. The stub captures the outbound request so we
// can assert the multipart shape, then returns whatever response the
// test wants — happy path, 5xx, 4xx, garbage body, transport error.
public class AndyDocsHttpClientTests
{
    private static readonly Uri BaseAddress = new("https://andy-docs.local/");

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }
        public string? CapturedBody { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; init; }
            = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            if (request.Content is not null)
            {
                CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return Responder(request);
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(_handler, disposeHandler: false)
            {
                BaseAddress = BaseAddress,
            };
        }
    }

    private static AndyDocsHttpClient MakeClient(HttpMessageHandler handler)
    {
        return new AndyDocsHttpClient(
            new StubFactory(handler),
            NullLogger<AndyDocsHttpClient>.Instance);
    }

    private static UploadRequest MakeRequest(byte[]? content = null) =>
        new(
            Content: content ?? new byte[] { 1, 2, 3 },
            MimeType: "application/pdf",
            Name: "report.pdf",
            Digest: new string('a', 64),
            Links: new[]
            {
                new DocumentLinkDescriptor("Run", Guid.NewGuid().ToString(), "Output"),
            });

    [Fact]
    public async Task UploadAsync_HappyPath_PostsToDocumentsPutAndReturnsDocsRef()
    {
        var documentId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        var handler = new CapturingHandler
        {
            Responder = _ =>
            {
                var json = JsonSerializer.Serialize(new { documentId, linkId });
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                return resp;
            },
        };
        var client = MakeClient(handler);

        var result = await client.UploadAsync(MakeRequest());

        result.Should().NotBeNull();
        result!.DocumentId.Should().Be(documentId);
        result.LinkId.Should().Be(linkId);

        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.Method.Should().Be(HttpMethod.Post);
        handler.CapturedRequest.RequestUri.Should().Be(new Uri(BaseAddress, "api/documents:put"));
    }

    [Fact]
    public async Task UploadAsync_HappyPath_BodyCarriesFileAndMetaParts()
    {
        var handler = new CapturingHandler
        {
            Responder = _ =>
            {
                var json = JsonSerializer.Serialize(new
                {
                    documentId = Guid.NewGuid(),
                    linkId = Guid.NewGuid(),
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            },
        };
        var client = MakeClient(handler);

        await client.UploadAsync(MakeRequest());

        handler.CapturedBody.Should().NotBeNullOrWhiteSpace();
        // Multipart body should carry both a `file` and `meta` part —
        // names match the andy-docs AJ5 contract.
        handler.CapturedBody.Should().Contain("name=file");
        handler.CapturedBody.Should().Contain("name=meta");
        handler.CapturedBody.Should().Contain("report.pdf");
    }

    [Fact]
    public async Task UploadAsync_MetaPart_CarriesNameAndLinks()
    {
        var handler = new CapturingHandler
        {
            Responder = _ =>
            {
                var json = JsonSerializer.Serialize(new
                {
                    documentId = Guid.NewGuid(),
                    linkId = Guid.NewGuid(),
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            },
        };
        var client = MakeClient(handler);

        var runId = Guid.NewGuid();
        var request = new UploadRequest(
            Content: new byte[] { 7, 7, 7 },
            MimeType: "application/json",
            Name: "out.json",
            Digest: new string('b', 64),
            Links: new[]
            {
                new DocumentLinkDescriptor("Run", runId.ToString(), "Output"),
            });

        await client.UploadAsync(request);

        handler.CapturedBody.Should().Contain("\"name\":\"out.json\"");
        handler.CapturedBody.Should().Contain("\"targetType\":\"Run\"");
        handler.CapturedBody.Should().Contain($"\"targetId\":\"{runId}\"");
        handler.CapturedBody.Should().Contain("\"role\":\"Output\"");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task UploadAsync_5xxAndTransient_ReturnsNull(HttpStatusCode status)
    {
        var handler = new CapturingHandler
        {
            Responder = _ => new HttpResponseMessage(status)
            {
                Content = new StringContent("upstream had a moment"),
            },
        };
        var client = MakeClient(handler);

        var result = await client.UploadAsync(MakeRequest());

        result.Should().BeNull(
            "best-effort contract: failures collapse to null (no throw)");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task UploadAsync_4xx_ReturnsNull(HttpStatusCode status)
    {
        var handler = new CapturingHandler
        {
            Responder = _ => new HttpResponseMessage(status),
        };
        var client = MakeClient(handler);

        var result = await client.UploadAsync(MakeRequest());

        result.Should().BeNull(
            "even non-retryable failures must not throw — caller is best-effort");
    }

    [Fact]
    public async Task UploadAsync_TransportError_ReturnsNull()
    {
        // Network-level failure (HttpRequestException from SendAsync).
        var handler = new ThrowingHandler(
            new HttpRequestException("connection refused"));
        var client = MakeClient(handler);

        var result = await client.UploadAsync(MakeRequest());

        result.Should().BeNull();
    }

    [Fact]
    public async Task UploadAsync_MalformedJsonResponse_ReturnsNull()
    {
        var handler = new CapturingHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{not-json", Encoding.UTF8, "application/json"),
            },
        };
        var client = MakeClient(handler);

        var result = await client.UploadAsync(MakeRequest());

        result.Should().BeNull();
    }

    [Fact]
    public async Task UploadAsync_ResponseWithMissingFields_ReturnsNull()
    {
        // documentId=00...0 → andy-docs returned a body but the shape
        // is incomplete (e.g. a bug or an old server). Treat as null
        // rather than fabricating an empty Guid DocsRef downstream.
        var handler = new CapturingHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"documentId\":\"00000000-0000-0000-0000-000000000000\",\"linkId\":\"00000000-0000-0000-0000-000000000000\"}",
                    Encoding.UTF8, "application/json"),
            },
        };
        var client = MakeClient(handler);

        var result = await client.UploadAsync(MakeRequest());

        result.Should().BeNull();
    }

    [Fact]
    public async Task UploadAsync_CallerCancellation_PropagatesOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new ThrowingHandler(
            new OperationCanceledException(cts.Token));
        var client = MakeClient(handler);

        var act = () => client.UploadAsync(MakeRequest(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;
        public ThrowingHandler(Exception exception) { _exception = exception; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw _exception;
        }
    }

    // ----- EX.7 (rivoli-ai/andy-containers#328) DownloadAsync -----
    //
    // Download is a two-hop fetch against the REAL andy-docs wire shape:
    //   GET /api/documents/{id}          -> DocumentDto { contentHash, ... }
    //   GET /api/documents/{id}/at/{h}:blob -> raw bytes
    // The router handler below impersonates both endpoints (including the
    // 404 / non-JSON / oversized failure modes the real server emits) so
    // the whole client chain is exercised, not just a mocked happy path.

    private sealed class RouterHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Route { get; init; }
            = _ => new HttpResponseMessage(HttpStatusCode.OK);
        public List<string> Paths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Route(request));
        }
    }

    private static HttpResponseMessage MetaResponse(string contentHash) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    id = Guid.NewGuid(),
                    parentFolderId = (Guid?)null,
                    name = "prior.json",
                    contentHash,
                    title = "Prior output",
                    content = (string?)null,
                    createdAt = DateTime.UtcNow,
                }),
                Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage BlobResponse(byte[] bytes, string mime = "text/plain", long? contentLength = null)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mime);
        if (contentLength is { } len) content.Headers.ContentLength = len;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    [Fact]
    public async Task DownloadAsync_HappyPath_ResolvesHashThenFetchesBlob()
    {
        var docId = Guid.NewGuid();
        var hash = new string('f', 64);
        var payload = Encoding.UTF8.GetBytes("prior task output");

        var handler = new RouterHandler
        {
            Route = req => req.RequestUri!.AbsolutePath.EndsWith(":blob")
                ? BlobResponse(payload, "application/json")
                : MetaResponse(hash),
        };
        var client = MakeClient(handler);

        var result = await client.DownloadAsync(docId, maxSizeBytes: 1024);

        result.IsSuccess.Should().BeTrue();
        result.Content.ToArray().Should().BeEquivalentTo(payload);
        result.MimeType.Should().Be("application/json");

        // Two hops, in order, against the real andy-docs paths.
        handler.Paths.Should().HaveCount(2);
        handler.Paths[0].Should().Be($"/api/documents/{docId}");
        handler.Paths[1].Should().Be($"/api/documents/{docId}/at/{hash}:blob");
    }

    [Fact]
    public async Task DownloadAsync_DocumentNotFound_ReturnsNotFound_WithoutBlobFetch()
    {
        var docId = Guid.NewGuid();
        var handler = new RouterHandler
        {
            Route = _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var client = MakeClient(handler);

        var result = await client.DownloadAsync(docId, maxSizeBytes: 1024);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(DocumentDownloadFailure.NotFound);
        handler.Paths.Should().ContainSingle("the metadata 404 must short-circuit before the blob hop");
    }

    [Fact]
    public async Task DownloadAsync_BlobNotFound_ReturnsNotFound()
    {
        var docId = Guid.NewGuid();
        var hash = new string('a', 64);
        var handler = new RouterHandler
        {
            Route = req => req.RequestUri!.AbsolutePath.EndsWith(":blob")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : MetaResponse(hash),
        };
        var client = MakeClient(handler);

        var result = await client.DownloadAsync(docId, maxSizeBytes: 1024);

        result.Failure.Should().Be(DocumentDownloadFailure.NotFound);
    }

    [Fact]
    public async Task DownloadAsync_DeclaredContentLengthOverCap_ReturnsTooLarge()
    {
        var docId = Guid.NewGuid();
        var hash = new string('a', 64);
        var handler = new RouterHandler
        {
            Route = req => req.RequestUri!.AbsolutePath.EndsWith(":blob")
                ? BlobResponse(new byte[] { 1, 2, 3 }, contentLength: 10_000)
                : MetaResponse(hash),
        };
        var client = MakeClient(handler);

        var result = await client.DownloadAsync(docId, maxSizeBytes: 100);

        result.Failure.Should().Be(DocumentDownloadFailure.TooLarge);
    }

    [Fact]
    public async Task DownloadAsync_StreamedBytesExceedCap_ReturnsTooLarge()
    {
        // No Content-Length advertised (server lied / chunked) but the
        // streamed body blows the cap — the capped reader must catch it.
        var docId = Guid.NewGuid();
        var hash = new string('a', 64);
        var big = new byte[500];
        var handler = new RouterHandler
        {
            Route = req => req.RequestUri!.AbsolutePath.EndsWith(":blob")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(big) }
                : MetaResponse(hash),
        };
        var client = MakeClient(handler);

        var result = await client.DownloadAsync(docId, maxSizeBytes: 100);

        result.Failure.Should().Be(DocumentDownloadFailure.TooLarge);
    }

    [Fact]
    public async Task DownloadAsync_MetadataMissingContentHash_ReturnsFetchFailed()
    {
        // Empty document (no head version) → no contentHash to fetch.
        var docId = Guid.NewGuid();
        var handler = new RouterHandler
        {
            Route = _ => MetaResponse(contentHash: ""),
        };
        var client = MakeClient(handler);

        var result = await client.DownloadAsync(docId, maxSizeBytes: 1024);

        result.Failure.Should().Be(DocumentDownloadFailure.FetchFailed);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task DownloadAsync_Metadata5xx_ReturnsFetchFailed(HttpStatusCode status)
    {
        var docId = Guid.NewGuid();
        var handler = new RouterHandler
        {
            Route = _ => new HttpResponseMessage(status) { Content = new StringContent("boom") },
        };
        var client = MakeClient(handler);

        var result = await client.DownloadAsync(docId, maxSizeBytes: 1024);

        result.Failure.Should().Be(DocumentDownloadFailure.FetchFailed);
    }

    [Fact]
    public async Task DownloadAsync_TransportError_ReturnsFetchFailed()
    {
        var client = MakeClient(new ThrowingHandler(new HttpRequestException("connection refused")));

        var result = await client.DownloadAsync(Guid.NewGuid(), maxSizeBytes: 1024);

        result.Failure.Should().Be(DocumentDownloadFailure.FetchFailed);
    }

    [Fact]
    public async Task DownloadAsync_EmptyDocumentId_Throws()
    {
        var client = MakeClient(new RouterHandler());

        var act = () => client.DownloadAsync(Guid.Empty, maxSizeBytes: 1024);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
