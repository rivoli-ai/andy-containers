using System.Net;
using System.Text;
using Andy.Containers.Abstractions.Images;
using Andy.Containers.Infrastructure.Registries;
using Andy.Containers.Infrastructure.Registries.Local;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Tests.Infrastructure.Registries;

// IM6 (rivoli-ai/andy-containers#260). Unit tests for LocalZotAdapter.
// HTTP is stubbed via a DelegatingHandler so the tests don't need a
// real zot — IM11 covers the end-to-end against a live registry.
//
// What this suite locks down:
// - Push: invokes the uploader; HEADs the registry afterward to read
//   the digest from Docker-Content-Digest; returns a RegistryReference
//   carrying that digest.
// - Exists: HEAD returning 200 ⇒ true; 404 ⇒ false; anything else ⇒
//   structured exception with a stable code.
// - ListReferences: tags/list + per-tag HEAD; 404 on the repo returns
//   empty (not throw); per-tag 404 is silently skipped (race with
//   delete); unsuccessful HEAD propagates.
// - Delete: 200/202/204 + 404 are all idempotent successes; other
//   statuses surface a structured exception.
public class LocalZotAdapterTests
{
    [Fact]
    public async Task PushAsync_DelegatesToUploaderAndReadsDigestFromHead()
    {
        var stub = new StubHandler(new Dictionary<string, StubHandler.Response>
        {
            // HEAD on the manifest returns the digest header zot
            // would produce after a successful push.
            ["HEAD /v2/foo/bar/manifests/v1"] = new(
                HttpStatusCode.OK,
                Headers: new Dictionary<string, string>
                {
                    ["Docker-Content-Digest"] = "sha256:abc123",
                }),
        });
        var uploader = new RecordingUploader();
        // Pin the no-rewrite (Linux/native daemon) baseline so this
        // assertion is OS-independent — the Docker Desktop rewrite is
        // exercised separately by
        // PushAsync_DockerDesktop_RewritesRemoteRefToHostDockerInternal.
        var adapter = NewAdapter(stub, uploader, pushTargetOptions: new PushTargetHostOptions
        {
            Mode = PushTargetHostRewriteMode.Auto,
            IsDockerDesktopOverride = false,
        });
        var artifact = MakeArtifact(localRef: "andy-build:tmp-123", specHash: "sha256:spec");

        var reference = await adapter.PushAsync(artifact, "foo/bar", "v1", CancellationToken.None);

        reference.Digest.Should().Be("sha256:abc123");
        reference.RegistryId.Should().Be("local-zot");
        reference.RepoPath.Should().Be("foo/bar");
        reference.Tag.Should().Be("v1");
        uploader.Pushed.Should().ContainSingle()
            .Which.Should().Be(("andy-build:tmp-123", "localhost:5050/foo/bar:v1"));
    }

    [Fact]
    public async Task PushAsync_Throws_WhenPostHeadOmitsDigestHeader()
    {
        var stub = new StubHandler(new Dictionary<string, StubHandler.Response>
        {
            ["HEAD /v2/foo/bar/manifests/v1"] = new(HttpStatusCode.OK, Headers: new Dictionary<string, string>()),
        });
        var adapter = NewAdapter(stub, new RecordingUploader());

        var act = async () => await adapter.PushAsync(
            MakeArtifact(localRef: "x", specHash: "sha256:y"),
            "foo/bar", "v1", CancellationToken.None);

        await act.Should().ThrowAsync<RegistryUploadException>()
            .Where(e => e.Code == "LocalZotAdapter.Push.MissingDigestHeader");
    }

    [Fact]
    public async Task ExistsAsync_TrueOn200_FalseOn404()
    {
        var stub = new StubHandler(new Dictionary<string, StubHandler.Response>
        {
            ["HEAD /v2/foo/manifests/sha256:there"] = new(HttpStatusCode.OK),
            ["HEAD /v2/foo/manifests/sha256:absent"] = new(HttpStatusCode.NotFound),
        });
        var adapter = NewAdapter(stub, new RecordingUploader());

        (await adapter.ExistsAsync("foo", "sha256:there", CancellationToken.None)).Should().BeTrue();
        (await adapter.ExistsAsync("foo", "sha256:absent", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ExistsAsync_ThrowsStructured_OnUnexpectedStatus()
    {
        var stub = new StubHandler(new Dictionary<string, StubHandler.Response>
        {
            ["HEAD /v2/foo/manifests/sha256:abc"] = new(HttpStatusCode.Unauthorized, Body: "auth required"),
        });
        var adapter = NewAdapter(stub, new RecordingUploader());

        var act = async () => await adapter.ExistsAsync("foo", "sha256:abc", CancellationToken.None);

        await act.Should().ThrowAsync<RegistryUploadException>()
            .Where(e => e.Code == "LocalZotAdapter.Exists.Http401");
    }

    [Fact]
    public async Task ListReferencesAsync_ReturnsEmptyOnRepoNotFound()
    {
        var stub = new StubHandler(new Dictionary<string, StubHandler.Response>
        {
            ["GET /v2/missing/tags/list"] = new(HttpStatusCode.NotFound),
        });
        var adapter = NewAdapter(stub, new RecordingUploader());

        var refs = await adapter.ListReferencesAsync("missing", CancellationToken.None);

        refs.Should().BeEmpty(
            "missing repo returns empty rather than throwing — matches the contract on IRegistryAdapter.");
    }

    [Fact]
    public async Task ListReferencesAsync_ReturnsTagsWithDigests()
    {
        var stub = new StubHandler(new Dictionary<string, StubHandler.Response>
        {
            ["GET /v2/foo/tags/list"] = new(
                HttpStatusCode.OK,
                Body: """{"name":"foo","tags":["v1","v2"]}"""),
            ["HEAD /v2/foo/manifests/v1"] = new(
                HttpStatusCode.OK,
                Headers: new Dictionary<string, string> { ["Docker-Content-Digest"] = "sha256:111" }),
            ["HEAD /v2/foo/manifests/v2"] = new(
                HttpStatusCode.OK,
                Headers: new Dictionary<string, string> { ["Docker-Content-Digest"] = "sha256:222" }),
        });
        var adapter = NewAdapter(stub, new RecordingUploader());

        var refs = await adapter.ListReferencesAsync("foo", CancellationToken.None);

        refs.Should().HaveCount(2);
        refs.Select(r => r.Tag).Should().BeEquivalentTo(["v1", "v2"]);
        refs.Single(r => r.Tag == "v1").Digest.Should().Be("sha256:111");
        refs.Single(r => r.Tag == "v2").Digest.Should().Be("sha256:222");
    }

    [Fact]
    public async Task ListReferencesAsync_SkipsTagsThatRaceWithDelete()
    {
        var stub = new StubHandler(new Dictionary<string, StubHandler.Response>
        {
            ["GET /v2/foo/tags/list"] = new(
                HttpStatusCode.OK,
                Body: """{"name":"foo","tags":["live","gone"]}"""),
            ["HEAD /v2/foo/manifests/live"] = new(
                HttpStatusCode.OK,
                Headers: new Dictionary<string, string> { ["Docker-Content-Digest"] = "sha256:111" }),
            ["HEAD /v2/foo/manifests/gone"] = new(HttpStatusCode.NotFound),
        });
        var adapter = NewAdapter(stub, new RecordingUploader());

        var refs = await adapter.ListReferencesAsync("foo", CancellationToken.None);

        refs.Should().ContainSingle().Which.Tag.Should().Be("live",
            "a tag deleted between tags/list and per-tag HEAD is silently skipped — racing with another caller's untag should not break the list.");
    }

    [Fact]
    public async Task DeleteAsync_IsIdempotent()
    {
        var stub = new StubHandler(new Dictionary<string, StubHandler.Response>
        {
            ["DELETE /v2/foo/manifests/sha256:abc"] = new(HttpStatusCode.NotFound),
        });
        var adapter = NewAdapter(stub, new RecordingUploader());

        var reference = new RegistryReference(
            Id: Guid.NewGuid(),
            RegistryId: "local-zot",
            RepoPath: "foo",
            Tag: "v1",
            Digest: "sha256:abc",
            PushedAt: DateTimeOffset.UtcNow,
            PushedBy: "test");

        await adapter.DeleteAsync(reference, CancellationToken.None);
        // No throw — already-gone is fine.
    }

    [Fact]
    public async Task DeleteAsync_AcceptsCommonSuccessStatuses()
    {
        foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.NoContent, HttpStatusCode.Accepted })
        {
            var stub = new StubHandler(new Dictionary<string, StubHandler.Response>
            {
                ["DELETE /v2/foo/manifests/sha256:abc"] = new(status),
            });
            var adapter = NewAdapter(stub, new RecordingUploader());

            var reference = new RegistryReference(
                Id: Guid.NewGuid(), RegistryId: "local-zot", RepoPath: "foo", Tag: "v1",
                Digest: "sha256:abc", PushedAt: DateTimeOffset.UtcNow, PushedBy: "test");

            await adapter.DeleteAsync(reference, CancellationToken.None);
        }
    }

    [Fact]
    public async Task DeleteAsync_ThrowsStructured_OnUnexpectedStatus()
    {
        var stub = new StubHandler(new Dictionary<string, StubHandler.Response>
        {
            ["DELETE /v2/foo/manifests/sha256:abc"] = new(HttpStatusCode.Forbidden, Body: "no perms"),
        });
        var adapter = NewAdapter(stub, new RecordingUploader());

        var reference = new RegistryReference(
            Id: Guid.NewGuid(), RegistryId: "local-zot", RepoPath: "foo", Tag: "v1",
            Digest: "sha256:abc", PushedAt: DateTimeOffset.UtcNow, PushedBy: "test");

        var act = async () => await adapter.DeleteAsync(reference, CancellationToken.None);

        await act.Should().ThrowAsync<RegistryUploadException>()
            .Where(e => e.Code == "LocalZotAdapter.Delete.Http403");
    }

    [Fact]
    public void Constructor_ExposesRegistryId()
    {
        var stub = new StubHandler(new Dictionary<string, StubHandler.Response>());
        var adapter = NewAdapter(stub, new RecordingUploader(), registryId: "team-zot");

        adapter.RegistryId.Should().Be("team-zot");
    }

    // Docker Desktop loopback gap (rivoli-ai/andy-containers). On Docker
    // Desktop the push/tag target must be host.docker.internal so the
    // `docker push` running inside the VM can reach the host's zot. The
    // HTTP client (post-push HEAD) still uses localhost. This test
    // proves the adapter rewrites the remote ref it hands the uploader,
    // while the HEAD it issues stays on localhost (the stub is keyed by
    // path only, so a wrong-host HEAD wouldn't even be routed).
    [Fact]
    public async Task PushAsync_DockerDesktop_RewritesRemoteRefToHostDockerInternal()
    {
        var stub = new StubHandler(new Dictionary<string, StubHandler.Response>
        {
            ["HEAD /v2/foo/bar/manifests/v1"] = new(
                HttpStatusCode.OK,
                Headers: new Dictionary<string, string>
                {
                    ["Docker-Content-Digest"] = "sha256:abc123",
                }),
        });
        var uploader = new RecordingUploader();
        var adapter = NewAdapter(stub, uploader, pushTargetOptions: new PushTargetHostOptions
        {
            Mode = PushTargetHostRewriteMode.Auto,
            IsDockerDesktopOverride = true,
        });

        await adapter.PushAsync(
            MakeArtifact(localRef: "andy-build:tmp-123", specHash: "sha256:spec"),
            "foo/bar", "v1", CancellationToken.None);

        uploader.Pushed.Should().ContainSingle()
            .Which.Should().Be(("andy-build:tmp-123", "host.docker.internal:5050/foo/bar:v1"));
    }

    [Fact]
    public async Task PushAsync_Linux_KeepsLocalhostRemoteRef()
    {
        var stub = new StubHandler(new Dictionary<string, StubHandler.Response>
        {
            ["HEAD /v2/foo/bar/manifests/v1"] = new(
                HttpStatusCode.OK,
                Headers: new Dictionary<string, string>
                {
                    ["Docker-Content-Digest"] = "sha256:abc123",
                }),
        });
        var uploader = new RecordingUploader();
        var adapter = NewAdapter(stub, uploader, pushTargetOptions: new PushTargetHostOptions
        {
            Mode = PushTargetHostRewriteMode.Auto,
            IsDockerDesktopOverride = false,
        });

        await adapter.PushAsync(
            MakeArtifact(localRef: "andy-build:tmp-123", specHash: "sha256:spec"),
            "foo/bar", "v1", CancellationToken.None);

        uploader.Pushed.Should().ContainSingle()
            .Which.Should().Be(("andy-build:tmp-123", "localhost:5050/foo/bar:v1"));
    }

    [Fact]
    public async Task PushAsync_WrapsDockerDesktopUnreachable_WithActionableHint()
    {
        var stub = new StubHandler(new Dictionary<string, StubHandler.Response>());
        var uploader = new ThrowingUploader(new RegistryUploadException(
            code: "DockerCliUploader.Push.NonZeroExit1",
            message: "docker exited with code 1",
            capturedOutput:
                "Get \"http://host.docker.internal:5050/v2/\": net/http: request canceled " +
                "while waiting for connection (Client.Timeout exceeded while awaiting headers)"));
        var adapter = NewAdapter(stub, uploader, pushTargetOptions: new PushTargetHostOptions
        {
            Mode = PushTargetHostRewriteMode.Auto,
            IsDockerDesktopOverride = true,
        });

        var act = async () => await adapter.PushAsync(
            MakeArtifact(localRef: "x", specHash: "sha256:y"),
            "foo/bar", "v1", CancellationToken.None);

        (await act.Should().ThrowAsync<RegistryUploadException>()
            .Where(e => e.Code == "LocalZotAdapter.Push.DockerDesktopUnreachable"))
            .Which.Message.Should().Contain("insecure-registries");
    }

    private static LocalZotAdapter NewAdapter(
        StubHandler handler,
        IRegistryUploader uploader,
        string registryId = "local-zot",
        PushTargetHostOptions? pushTargetOptions = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5050") };
        return new LocalZotAdapter(
            http, uploader, NullLogger<LocalZotAdapter>.Instance, registryId, pushTargetOptions);
    }

    private static BuildArtifact MakeArtifact(string localRef, string specHash)
        => new(
            Digest: string.Empty, // adapter resolves this from the registry
            MediaType: "application/vnd.oci.image.manifest.v1+json",
            SizeBytes: 100_000,
            SpecHash: specHash,
            LocalReference: localRef);

    /// <summary>
    /// Records every push call so tests can assert what the adapter
    /// asked the uploader to do.
    /// </summary>
    private sealed class RecordingUploader : IRegistryUploader
    {
        public List<(string Local, string Remote)> Pushed { get; } = [];

        public Task PushAsync(string localReference, string remoteReference, CancellationToken ct)
        {
            Pushed.Add((localReference, remoteReference));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Uploader that always throws a supplied exception — exercises the
    /// adapter's Docker Desktop failure-wrapping path.
    /// </summary>
    private sealed class ThrowingUploader : IRegistryUploader
    {
        private readonly RegistryUploadException _ex;
        public ThrowingUploader(RegistryUploadException ex) { _ex = ex; }

        public Task PushAsync(string localReference, string remoteReference, CancellationToken ct)
            => throw _ex;
    }

    /// <summary>
    /// DelegatingHandler that returns canned responses keyed by
    /// <c>"{METHOD} {path-with-query}"</c>. Unknown keys throw to
    /// surface unintended HTTP traffic.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Response> _routes;
        public StubHandler(Dictionary<string, Response> routes) { _routes = routes; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var key = $"{request.Method.Method} {path}";
            if (!_routes.TryGetValue(key, out var response))
            {
                throw new InvalidOperationException(
                    $"Unstubbed HTTP request: {key}. Add a route to the StubHandler in the test.");
            }

            var msg = new HttpResponseMessage(response.StatusCode);
            if (response.Headers is not null)
            {
                foreach (var (name, value) in response.Headers)
                {
                    // Headers like Docker-Content-Digest live on the
                    // response (not content) — TryAddWithoutValidation
                    // is the right path for free-form custom headers.
                    msg.Headers.TryAddWithoutValidation(name, value);
                }
            }
            if (response.Body is not null)
            {
                msg.Content = new StringContent(
                    response.Body,
                    Encoding.UTF8,
                    response.ContentType ?? "application/json");
            }
            return Task.FromResult(msg);
        }

        public sealed record Response(
            HttpStatusCode StatusCode,
            string? Body = null,
            string? ContentType = null,
            Dictionary<string, string>? Headers = null);
    }
}
