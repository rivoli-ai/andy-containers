// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Andy.Containers.Abstractions.Images;
using Andy.Containers.Infrastructure.Registries.Local;
using Andy.Containers.Integration.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Integration.Tests.RoundTrip;

// IM11 (rivoli-ai/andy-containers#265). Round-trip test:
// LocalZotAdapter against a real zot. Populates zot directly via the
// OCI Distribution v1.1 HTTP API (PUT manifest + blobs) — that
// bypasses the docker push path which has known incompatibilities
// with zot's default config around BuildKit's auxiliary manifests
// (provenance / SBOM). What's under test here is the *adapter's*
// HTTP read/exists/list/delete behaviour against a real registry,
// not the engine's push behaviour (which the unit tests already
// pin against bash stubs).
//
// Gated on Docker availability via ZotContainerFixture.IsAvailable.
public class LocalZotAdapterRoundTripTests : IClassFixture<ZotContainerFixture>
{
    private readonly ZotContainerFixture _zot;

    public LocalZotAdapterRoundTripTests(ZotContainerFixture zot) { _zot = zot; }

    [Fact]
    public async Task ExistsListDelete_AgainstRealZot()
    {
        if (!_zot.IsAvailable)
        {
            return; // Docker unavailable — skip
        }

        using var http = new HttpClient { BaseAddress = new Uri(_zot.BaseUrl) };

        // 1. Populate zot directly via the OCI Distribution v1.1
        //    upload protocol. Two blobs (config + an empty layer) +
        //    a manifest tying them together.
        var (manifestDigest, configDigest, layerDigest) =
            await UploadMinimalImageAsync(http, repo: "im11-roundtrip", tag: "v1");

        // 2. Construct the adapter pointed at the same zot.
        using var adapterHttp = new HttpClient { BaseAddress = new Uri(_zot.BaseUrl) };
        var uploader = new DockerCliUploader(NullLogger<DockerCliUploader>.Instance);
        var adapter = new LocalZotAdapter(
            adapterHttp,
            uploader,
            NullLogger<LocalZotAdapter>.Instance,
            registryId: "test-zot");

        // 3. ExistsAsync — by digest should return true; by a
        //    fabricated digest should return false.
        (await adapter.ExistsAsync("im11-roundtrip", manifestDigest, CancellationToken.None))
            .Should().BeTrue("the manifest we PUT exists at this digest.");
        (await adapter.ExistsAsync("im11-roundtrip", "sha256:" + new string('0', 64), CancellationToken.None))
            .Should().BeFalse("an unrelated digest returns false (404 from zot).");

        // 4. ListReferencesAsync — tags/list + per-tag HEAD should
        //    surface the tag we PUT, with the digest matching.
        var refs = await adapter.ListReferencesAsync("im11-roundtrip", CancellationToken.None);
        refs.Should().HaveCount(1);
        refs[0].Tag.Should().Be("v1");
        refs[0].Digest.Should().Be(manifestDigest,
            "tags/list + HEAD round-trip yields the same digest we computed locally.");

        // 5. DeleteAsync — removes the tag. Subsequent list returns empty.
        var reference = new RegistryReference(
            Id: Guid.NewGuid(),
            RegistryId: "test-zot",
            RepoPath: "im11-roundtrip",
            Tag: "v1",
            Digest: manifestDigest,
            PushedAt: DateTimeOffset.UtcNow,
            PushedBy: "test");
        await adapter.DeleteAsync(reference, CancellationToken.None);

        var refsAfter = await adapter.ListReferencesAsync("im11-roundtrip", CancellationToken.None);
        refsAfter.Should().BeEmpty(
            "after DELETE the tag is gone — the registry's tags/list no longer mentions it.");
    }

    [Fact]
    public async Task ListReferencesAsync_EmptyForUnknownRepo()
    {
        if (!_zot.IsAvailable)
        {
            return;
        }

        using var adapterHttp = new HttpClient { BaseAddress = new Uri(_zot.BaseUrl) };
        var adapter = new LocalZotAdapter(
            adapterHttp,
            new DockerCliUploader(NullLogger<DockerCliUploader>.Instance),
            NullLogger<LocalZotAdapter>.Instance,
            registryId: "test-zot");

        var refs = await adapter.ListReferencesAsync("repo-that-never-existed", CancellationToken.None);

        refs.Should().BeEmpty(
            "missing repos return empty per the IRegistryAdapter contract — not a 404 surfaced as exception.");
    }

    /// <summary>
    /// The actual production push path: build a real image, push it
    /// via <see cref="DockerCliUploader"/> (shells out to
    /// <c>docker tag</c> + <c>docker push</c>), let the adapter read
    /// the post-push digest via HEAD. This is what M1.9 builds will
    /// do; if any step regresses, builds fail with "manifest invalid"
    /// — the bug rivoli-ai/conductor#1028 fixed by enabling
    /// <c>http.compat: ["docker2s2"]</c>. The
    /// <see cref="ZotContainerFixture"/> mirrors that config so the
    /// test exercises the same path that production runs.
    /// </summary>
    [Fact]
    public async Task PushPath_DockerCliUploaderToRealZot()
    {
        if (!_zot.IsAvailable)
        {
            return;
        }

        var contextDir = Directory.CreateTempSubdirectory("im11-pushpath-").FullName;
        var localTag = $"andy-containers-im11-push-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(contextDir, "Dockerfile"),
                "FROM hello-world\n");

            // --provenance / --sbom off — the BuildKit attached
            // manifests confuse OCI-strict registries even when
            // compat is enabled. Production uses these flags too.
            await RunDockerCliAsync([
                "buildx", "build",
                "--provenance=false", "--sbom=false",
                "--output=type=docker",
                "-t", localTag,
                contextDir,
            ]);

            using var http = new HttpClient { BaseAddress = new Uri(_zot.BaseUrl) };
            var adapter = new LocalZotAdapter(
                http,
                new DockerCliUploader(NullLogger<DockerCliUploader>.Instance),
                NullLogger<LocalZotAdapter>.Instance,
                registryId: "test-zot");

            var artifact = new BuildArtifact(
                Digest: string.Empty,
                MediaType: "application/vnd.docker.distribution.manifest.v2+json",
                SizeBytes: 0,
                SpecHash: "sha256:test-spec-pushpath",
                LocalReference: localTag);

            var reference = await adapter.PushAsync(
                artifact,
                repoPath: "im11-push",
                tag: "v1",
                CancellationToken.None);

            reference.Digest.Should().StartWith("sha256:",
                "the post-push HEAD must yield Docker-Content-Digest from zot.");

            (await adapter.ExistsAsync("im11-push", reference.Digest, CancellationToken.None))
                .Should().BeTrue("the manifest just pushed must be reachable by digest.");

            var refs = await adapter.ListReferencesAsync("im11-push", CancellationToken.None);
            refs.Should().ContainSingle()
                .Which.Digest.Should().Be(reference.Digest);

            await RunDockerCliAsync(["rmi", "-f", localTag]);
        }
        finally
        {
            try { Directory.Delete(contextDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// P1F3 (rivoli-ai/andy-containers#276). Parallel to
    /// <see cref="PushPath_DockerCliUploaderToRealZot"/> but exercises
    /// the Apple Containers path: build via <c>container build</c>,
    /// then push via <see cref="AppleContainersUploader"/>. Gated on
    /// the `container` CLI being on PATH (macOS 26+). Also needs zot
    /// available for the registry side — when Docker isn't running
    /// the test short-circuits like its docker sibling.
    /// </summary>
    [AppleContainerCliFact]
    public async Task PushPath_AppleContainersUploaderToRealZot()
    {
        if (!_zot.IsAvailable)
        {
            return;
        }

        var contextDir = Directory.CreateTempSubdirectory("p1f3-pushpath-").FullName;
        var localTag = $"andy-containers-p1f3-push-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(contextDir, "Dockerfile"),
                "FROM hello-world\n");

            await RunContainerCliAsync([
                "build",
                "-t", localTag,
                contextDir,
            ]);

            using var http = new HttpClient { BaseAddress = new Uri(_zot.BaseUrl) };
            var adapter = new LocalZotAdapter(
                http,
                new AppleContainersUploader(NullLogger<AppleContainersUploader>.Instance),
                NullLogger<LocalZotAdapter>.Instance,
                registryId: "test-zot");

            var artifact = new BuildArtifact(
                Digest: string.Empty,
                MediaType: "application/vnd.oci.image.manifest.v1+json",
                SizeBytes: 0,
                SpecHash: "sha256:test-spec-apple-pushpath",
                LocalReference: localTag);

            var reference = await adapter.PushAsync(
                artifact,
                repoPath: "p1f3-push",
                tag: "v1",
                CancellationToken.None);

            reference.Digest.Should().StartWith("sha256:",
                "the post-push HEAD must yield Docker-Content-Digest from zot when pushing via Apple Containers too.");

            (await adapter.ExistsAsync("p1f3-push", reference.Digest, CancellationToken.None))
                .Should().BeTrue("the manifest just pushed must be reachable by digest.");

            // Best-effort cleanup; Apple's `container images delete`
            // is the rough equivalent of `docker rmi -f`. Ignore the
            // exit code so a stale image left behind from a prior run
            // doesn't fail the test.
            try { await RunContainerCliAsync(["images", "delete", localTag]); }
            catch { /* best-effort */ }
        }
        finally
        {
            try { Directory.Delete(contextDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static async Task RunContainerCliAsync(string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "container",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var proc = new System.Diagnostics.Process { StartInfo = psi };
        proc.Start();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"container {string.Join(' ', args)} exited {proc.ExitCode}: {await stderrTask}");
        }
    }

    private static async Task RunDockerCliAsync(string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var proc = new System.Diagnostics.Process { StartInfo = psi };
        proc.Start();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker {string.Join(' ', args)} exited {proc.ExitCode}: {await stderrTask}");
        }
    }

    /// <summary>
    /// Push a minimal valid OCI image manifest to zot via the
    /// OCI Distribution v1.1 protocol. Returns the digests so the
    /// caller can assert against them.
    /// </summary>
    private static async Task<(string ManifestDigest, string ConfigDigest, string LayerDigest)>
        UploadMinimalImageAsync(HttpClient http, string repo, string tag)
    {
        // Empty layer (a zero-byte tar.gz). zot accepts an empty
        // gzipped tar as a valid layer for the purposes of manifest
        // validation.
        var layerBytes = GzipEmptyTar();
        var layerDigest = Sha256Digest(layerBytes);
        await UploadBlobAsync(http, repo, layerDigest, layerBytes);

        // Config blob — a minimal OCI image config. zot validates
        // the JSON has the expected shape (architecture, os,
        // rootfs, history) so the bytes have to be reasonable.
        var configJson =
            $$"""
            {
              "architecture": "amd64",
              "os": "linux",
              "rootfs": {
                "type": "layers",
                "diff_ids": ["{{layerDigest}}"]
              },
              "history": [
                { "created": "2026-05-07T00:00:00Z", "comment": "im11 round-trip test" }
              ]
            }
            """;
        var configBytes = Encoding.UTF8.GetBytes(configJson);
        var configDigest = Sha256Digest(configBytes);
        await UploadBlobAsync(http, repo, configDigest, configBytes);

        // Manifest — references the config + single layer.
        var manifestJson =
            $$"""
            {
              "schemaVersion": 2,
              "mediaType": "application/vnd.oci.image.manifest.v1+json",
              "config": {
                "mediaType": "application/vnd.oci.image.config.v1+json",
                "size": {{configBytes.Length}},
                "digest": "{{configDigest}}"
              },
              "layers": [
                {
                  "mediaType": "application/vnd.oci.image.layer.v1.tar+gzip",
                  "size": {{layerBytes.Length}},
                  "digest": "{{layerDigest}}"
                }
              ]
            }
            """;
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        var manifestDigest = Sha256Digest(manifestBytes);

        // PUT /v2/<name>/manifests/<reference>
        using var req = new HttpRequestMessage(HttpMethod.Put, $"v2/{repo}/manifests/{tag}")
        {
            Content = new ByteArrayContent(manifestBytes),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.oci.image.manifest.v1+json");
        var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"manifest PUT failed: {(int)resp.StatusCode} {resp.StatusCode}: {body}");
        }

        return (manifestDigest, configDigest, layerDigest);
    }

    private static async Task UploadBlobAsync(HttpClient http, string repo, string digest, byte[] bytes)
    {
        // OCI distribution: POST /v2/<name>/blobs/uploads/ → 202 with
        // Location header for the upload session, then PUT
        // <Location>?digest=<digest> with the blob bytes to monolithic-upload.
        using var initResp = await http.PostAsync($"v2/{repo}/blobs/uploads/", content: null);
        if (!initResp.IsSuccessStatusCode && initResp.StatusCode != System.Net.HttpStatusCode.Accepted)
        {
            var body = await initResp.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"blob upload init failed: {(int)initResp.StatusCode}: {body}");
        }

        var location = initResp.Headers.Location?.ToString()
            ?? throw new InvalidOperationException("blob upload init: missing Location header");

        // Location may be relative or absolute; the standard practice
        // is to append `?digest=<digest>` (or `&digest=<digest>` when
        // a query is already present).
        var separator = location.Contains('?') ? "&" : "?";
        var uploadUri = $"{location}{separator}digest={Uri.EscapeDataString(digest)}";

        using var putReq = new HttpRequestMessage(HttpMethod.Put, uploadUri)
        {
            Content = new ByteArrayContent(bytes),
        };
        putReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var putResp = await http.SendAsync(putReq);
        if (!putResp.IsSuccessStatusCode)
        {
            var body = await putResp.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"blob PUT failed: {(int)putResp.StatusCode}: {body}");
        }
    }

    private static byte[] GzipEmptyTar()
    {
        // Empty tar (1024 bytes of NUL-padded EOF blocks per tar's
        // end-of-archive convention) compressed with gzip. zot
        // accepts this as a valid layer.
        var emptyTar = new byte[1024];
        using var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(emptyTar, 0, emptyTar.Length);
        }
        return ms.ToArray();
    }

    private static string Sha256Digest(byte[] bytes)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
