using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Containers.Abstractions.Images;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Infrastructure.Registries.Local;

/// <summary>
/// First concrete <see cref="IRegistryAdapter"/> — talks the
/// OCI Distribution v1.1 surface to a local zot registry. Pushes
/// delegate to an injected <see cref="IRegistryUploader"/> (because
/// "move bytes out of the build engine" is engine-coupled and lives
/// outside the registry adapter); reads, existence checks, and tag
/// deletion go straight to zot's HTTP API.
/// </summary>
/// <remarks>
/// IM6 (rivoli-ai/andy-containers#260). Defaults to the
/// <c>local-zot</c> id but accepts any id at construction time so a
/// host with multiple local zots (rare but legal) can register more
/// than one adapter. The HTTP client is supplied by the named
/// <see cref="IHttpClientFactory"/> client <c>"andy-containers.local-zot"</c>
/// so test fixtures can substitute it cleanly.
/// </remarks>
public sealed class LocalZotAdapter : IRegistryAdapter
{
    private readonly HttpClient _http;
    private readonly IRegistryUploader _uploader;
    private readonly ILogger<LocalZotAdapter> _logger;
    private readonly PushTargetHostOptions _pushTargetOptions;

    public LocalZotAdapter(
        HttpClient http,
        IRegistryUploader uploader,
        ILogger<LocalZotAdapter> logger,
        string registryId = "local-zot",
        PushTargetHostOptions? pushTargetOptions = null)
    {
        _http = http;
        _uploader = uploader;
        _logger = logger;
        RegistryId = registryId;
        _pushTargetOptions = pushTargetOptions ?? new PushTargetHostOptions();
    }

    public string RegistryId { get; }

    public async Task<RegistryReference> PushAsync(
        BuildArtifact artifact,
        string repoPath,
        string tag,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        // The remote reference is what the engine's `push` command
        // talks to — the registry's host:port plus the repo path
        // and tag. zot at localhost:5050 accepts repo paths with
        // slashes (foo/bar/baz) and standard tag syntax.
        //
        // Docker Desktop loopback gap: `docker push` runs inside the
        // Docker Desktop VM, where `localhost` is the VM — not the host
        // running zot. Rewrite the push/tag authority to a VM-reachable
        // host (host.docker.internal) on Docker Desktop while the HTTP
        // client (_http) keeps using the host's localhost for the
        // post-push HEAD. See PushTargetHostResolver.
        var baseAuthority = ExtractAuthority(_http.BaseAddress);
        var targetResolution = PushTargetHostResolver.Resolve(baseAuthority, _pushTargetOptions);
        var remoteRef = $"{targetResolution.TargetAuthority}/{repoPath}:{tag}";

        _logger.LogInformation(
            "LocalZotAdapter.Push.Start registryId={RegistryId} local={Local} remote={Remote} rewritten={Rewritten}",
            RegistryId, artifact.LocalReference, remoteRef, targetResolution.WasRewritten);

        try
        {
            await _uploader.PushAsync(artifact.LocalReference, remoteRef, ct);
        }
        catch (RegistryUploadException ex)
        {
            var hint = RegistryPushFailureDiagnostics.BuildHint(
                targetResolution.TargetAuthority, ex.CapturedOutput ?? ex.Message, targetResolution.WasRewritten);
            if (hint is null)
            {
                throw;
            }

            _logger.LogError(
                "LocalZotAdapter.Push.DockerDesktopMisconfig registryId={RegistryId} remote={Remote}: {Hint}",
                RegistryId, remoteRef, hint);

            throw new RegistryUploadException(
                code: "LocalZotAdapter.Push.DockerDesktopUnreachable",
                message: $"push of '{remoteRef}' to '{RegistryId}' failed: {ex.Message}\n\n{hint}",
                capturedOutput: ex.CapturedOutput,
                innerException: ex);
        }

        // Resolve the digest authoritatively from the registry's
        // own HEAD response. Docker-Content-Digest is the contract.
        var pushedDigest = await ResolveDigestAsync(repoPath, tag, ct);

        _logger.LogInformation(
            "LocalZotAdapter.Push.Done registryId={RegistryId} digest={Digest}",
            RegistryId, pushedDigest);

        return new RegistryReference(
            Id: Guid.NewGuid(),
            RegistryId: RegistryId,
            RepoPath: repoPath,
            Tag: tag,
            Digest: pushedDigest,
            PushedAt: DateTimeOffset.UtcNow,
            PushedBy: string.Empty);
    }

    public async Task<bool> ExistsAsync(string repoPath, string digest, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);

        // OCI Distribution v1.1 §3.1 — HEAD on the manifest endpoint
        // returns 200 if the named manifest exists, 404 otherwise.
        // Accept header tells the registry which manifest media types
        // we'll accept; without it some registries 406. zot is
        // permissive but we send the standard list anyway.
        using var request = new HttpRequestMessage(
            HttpMethod.Head,
            $"v2/{repoPath}/manifests/{digest}");
        AddOciManifestAccept(request);

        try
        {
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                return true;
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
            await ThrowForStatusAsync("LocalZotAdapter.Exists", response, repoPath, ct);
            return false; // unreachable; ThrowForStatusAsync always throws
        }
        catch (HttpRequestException ex)
        {
            throw new RegistryUploadException(
                code: "LocalZotAdapter.Exists.Network",
                message: $"network failure checking {repoPath}@{digest} on '{RegistryId}': {ex.Message}",
                innerException: ex);
        }
    }

    public async Task<IReadOnlyList<RegistryReference>> ListReferencesAsync(
        string repoPath,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);

        // OCI Distribution v1.1 §3.5 — GET tags/list returns the tag
        // names. To produce a full RegistryReference we'd need to
        // resolve each tag to a digest via HEAD; doing that lazily
        // here keeps the round-trips proportional to what the caller
        // actually needs. (Callers that only want tag names can pull
        // them off the result without forcing the digest fetch.)
        using var listResponse = await _http.GetAsync($"v2/{repoPath}/tags/list", ct);
        if (listResponse.StatusCode == HttpStatusCode.NotFound)
        {
            // Repo doesn't exist — return empty rather than throwing,
            // matching the "list returns empty" expectation of
            // IRegistryAdapter.
            return [];
        }
        if (!listResponse.IsSuccessStatusCode)
        {
            await ThrowForStatusAsync("LocalZotAdapter.ListReferences", listResponse, repoPath, ct);
        }

        var payload = await listResponse.Content.ReadFromJsonAsync<TagsListResponse>(ct)
            ?? new TagsListResponse(repoPath, []);

        var refs = new List<RegistryReference>(payload.Tags.Count);
        foreach (var tag in payload.Tags)
        {
            using var headRequest = new HttpRequestMessage(
                HttpMethod.Head,
                $"v2/{repoPath}/manifests/{tag}");
            AddOciManifestAccept(headRequest);

            using var headResponse = await _http.SendAsync(
                headRequest, HttpCompletionOption.ResponseHeadersRead, ct);

            if (headResponse.StatusCode == HttpStatusCode.NotFound)
            {
                // Tag listed but manifest gone — race with a delete.
                // Skip rather than failing the whole list.
                continue;
            }
            if (!headResponse.IsSuccessStatusCode)
            {
                await ThrowForStatusAsync("LocalZotAdapter.ListReferences.HeadTag", headResponse, $"{repoPath}:{tag}", ct);
            }

            var digest = headResponse.Headers.TryGetValues("Docker-Content-Digest", out var values)
                ? values.FirstOrDefault() ?? string.Empty
                : string.Empty;

            refs.Add(new RegistryReference(
                Id: Guid.NewGuid(),
                RegistryId: RegistryId,
                RepoPath: repoPath,
                Tag: tag,
                Digest: digest,
                PushedAt: DateTimeOffset.MinValue,
                PushedBy: string.Empty));
        }

        return refs;
    }

    public async Task DeleteAsync(RegistryReference reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.RepoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.Digest);

        // OCI Distribution v1.1 §6 — DELETE on the manifest URL
        // removes the tag binding. The underlying blobs stay until
        // registry-side garbage collection runs, which is the right
        // separation: the adapter can untag without making
        // policy-level decisions about reclamation.
        using var response = await _http.DeleteAsync(
            $"v2/{reference.RepoPath}/manifests/{reference.Digest}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Idempotent: already gone is fine.
            return;
        }
        if (response.StatusCode == HttpStatusCode.Accepted ||
            response.StatusCode == HttpStatusCode.NoContent ||
            response.StatusCode == HttpStatusCode.OK)
        {
            return;
        }
        await ThrowForStatusAsync(
            "LocalZotAdapter.Delete",
            response,
            $"{reference.RepoPath}@{reference.Digest}",
            ct);
    }

    private async Task<string> ResolveDigestAsync(string repoPath, string tag, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Head,
            $"v2/{repoPath}/manifests/{tag}");
        AddOciManifestAccept(request);

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new RegistryUploadException(
                code: $"LocalZotAdapter.Push.PostHeadHttp{(int)response.StatusCode}",
                message: $"push of '{repoPath}:{tag}' to '{RegistryId}' completed but the post-push HEAD returned HTTP {(int)response.StatusCode} — the bytes may not have made it.",
                capturedOutput: null);
        }

        if (!response.Headers.TryGetValues("Docker-Content-Digest", out var values))
        {
            throw new RegistryUploadException(
                code: "LocalZotAdapter.Push.MissingDigestHeader",
                message: $"registry '{RegistryId}' did not return a Docker-Content-Digest header for '{repoPath}:{tag}' — cannot record the push.");
        }

        var digest = values.FirstOrDefault();
        if (string.IsNullOrEmpty(digest))
        {
            throw new RegistryUploadException(
                code: "LocalZotAdapter.Push.EmptyDigestHeader",
                message: $"registry '{RegistryId}' returned an empty Docker-Content-Digest header for '{repoPath}:{tag}'.");
        }
        return digest;
    }

    private static void AddOciManifestAccept(HttpRequestMessage request)
    {
        // Standard OCI Image / Docker manifest media types. Listing
        // both keeps us compatible with Docker-style images served
        // through zot as well as native OCI ones.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            "application/vnd.oci.image.manifest.v1+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            "application/vnd.oci.image.index.v1+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            "application/vnd.docker.distribution.manifest.v2+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            "application/vnd.docker.distribution.manifest.list.v2+json"));
    }

    private static async Task ThrowForStatusAsync(
        string codePrefix,
        HttpResponseMessage response,
        string subject,
        CancellationToken ct)
    {
        var body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception)
        {
            // If we can't read the body, surface the status alone —
            // never let response-reading failures mask the real
            // upstream error.
        }

        var status = (int)response.StatusCode;
        throw new RegistryUploadException(
            code: $"{codePrefix}.Http{status}",
            message: $"{codePrefix} got HTTP {status} for '{subject}': {Truncate(body, 500)}",
            capturedOutput: body);
    }

    private static string ExtractAuthority(Uri? baseAddress)
    {
        if (baseAddress is null)
        {
            // Caller misconfigured the HttpClient. Without a base
            // address we can't construct the remote ref correctly.
            throw new InvalidOperationException(
                "LocalZotAdapter requires HttpClient.BaseAddress to be set to the registry root (e.g. http://localhost:5050).");
        }
        return baseAddress.IsDefaultPort
            ? baseAddress.Host
            : $"{baseAddress.Host}:{baseAddress.Port}";
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private sealed record TagsListResponse(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("tags")] List<string> Tags);
}
