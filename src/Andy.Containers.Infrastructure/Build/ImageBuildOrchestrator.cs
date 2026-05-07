using Andy.Containers.Abstractions.Images;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Registries;
using Andy.Containers.Models;
using Andy.Containers.Models.ImageManagement;
using Andy.Containers.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Infrastructure.Build;

/// <summary>
/// EF + adapter-backed implementation of
/// <see cref="IImageBuildOrchestrator"/>. Loads the template, checks
/// the content-addressable cache, builds + pushes when needed, and
/// persists the resulting <see cref="BuildArtifactEntity"/> +
/// <see cref="RegistryReferenceEntity"/> rows.
/// </summary>
/// <remarks>
/// IM8 (rivoli-ai/andy-containers#262). The cache-hit short-circuit
/// is the critical contract — when a build with the same template +
/// spec hash exists AND has a reference in the target registry,
/// return <see cref="BuildResultStatus.Cached"/> immediately
/// without invoking the backend. The full async / SSE machinery
/// lands in IM9; for IM8 the orchestrator runs synchronously in
/// the request thread.
/// </remarks>
public sealed class ImageBuildOrchestrator : IImageBuildOrchestrator
{
    private readonly ContainersDbContext _db;
    private readonly IBuildArtifactStore _store;
    private readonly IRegistryConfiguration _registries;
    private readonly IEnumerable<IRegistryAdapter> _adapters;
    private readonly IBuildBackend _backend;
    private readonly ILogger<ImageBuildOrchestrator> _logger;

    public ImageBuildOrchestrator(
        ContainersDbContext db,
        IBuildArtifactStore store,
        IRegistryConfiguration registries,
        IEnumerable<IRegistryAdapter> adapters,
        IBuildBackend backend,
        ILogger<ImageBuildOrchestrator> logger)
    {
        _db = db;
        _store = store;
        _registries = registries;
        _adapters = adapters;
        _backend = backend;
        _logger = logger;
    }

    public async Task<BuildResult?> TryCacheHitAsync(
        ImageBuildRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var template = await _db.Templates
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);
        if (template is null || string.IsNullOrEmpty(template.SpecHash))
        {
            return null;
        }
        var registryId = request.RegistryId ?? _registries.PrimaryRegistryId;
        var cached = await _store.GetBySpecHashAsync(template.Id, template.SpecHash, ct);
        if (cached is null)
        {
            return null;
        }
        var matching = cached.References.FirstOrDefault(r => r.RegistryId == registryId);
        if (matching is null)
        {
            // Same digest exists but isn't pushed to the requested
            // registry — the full BuildAsync path may rebuild and
            // push (or repush in a later iteration). Returning null
            // here keeps the fast path narrowly correct.
            return null;
        }

        return new BuildResult
        {
            BuildId = Guid.NewGuid(),
            Status = BuildResultStatus.Cached,
            Digest = cached.Digest,
            References = cached.References
                .Select(r => new BuildResultReference(
                    r.RegistryId, r.RepoPath, r.Tag,
                    new DateTimeOffset(r.PushedAt, TimeSpan.Zero)))
                .ToList(),
        };
    }

    public async Task<BuildResult> BuildAsync(
        ImageBuildRequest request,
        IProgress<BuildProgressEvent> progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        var buildId = Guid.NewGuid();

        var template = await _db.Templates
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);
        if (template is null)
        {
            return new BuildResult
            {
                BuildId = buildId,
                Status = BuildResultStatus.Failed,
                ErrorCode = "template.not_found",
                ErrorMessage = $"no template with id '{request.TemplateId}'.",
            };
        }
        if (string.IsNullOrEmpty(template.SpecHash))
        {
            // Pre-IM8 templates (no spec hash) fall through to the
            // build path rather than treating the missing hash as
            // an error. They just won't get cache hits.
            _logger.LogWarning(
                "ImageBuildOrchestrator template {Code} has no SpecHash — cache will always miss until the template is re-registered.",
                template.Code);
        }

        var registryId = request.RegistryId ?? _registries.PrimaryRegistryId;
        var adapter = ResolveAdapter(registryId);
        if (adapter is null)
        {
            return new BuildResult
            {
                BuildId = buildId,
                Status = BuildResultStatus.Failed,
                ErrorCode = "registry.not_configured",
                ErrorMessage = $"no IRegistryAdapter registered for id '{registryId}'.",
            };
        }

        if (!request.Force && !string.IsNullOrEmpty(template.SpecHash))
        {
            var cached = await _store.GetBySpecHashAsync(template.Id, template.SpecHash, ct);
            if (cached is not null)
            {
                var matching = cached.References.FirstOrDefault(r => r.RegistryId == registryId);
                if (matching is not null)
                {
                    _logger.LogInformation(
                        "ImageBuildOrchestrator cache hit for template {Code} specHash {Hash} on registry {Registry}.",
                        template.Code, template.SpecHash, registryId);
                    return new BuildResult
                    {
                        BuildId = buildId,
                        Status = BuildResultStatus.Cached,
                        Digest = cached.Digest,
                        References = cached.References
                            .Select(r => new BuildResultReference(
                                r.RegistryId, r.RepoPath, r.Tag,
                                new DateTimeOffset(r.PushedAt, TimeSpan.Zero)))
                            .ToList(),
                    };
                }
                // The artifact exists but hasn't been pushed to the
                // requested registry. Re-pushing without rebuild
                // requires the local image bytes which we no longer
                // have at this point — fall through to a rebuild.
                // IM11 / a follow-up may add an "extract from
                // registry, re-push" path. Tracked as a known gap.
                _logger.LogInformation(
                    "ImageBuildOrchestrator cache hit on digest but missing reference in registry {Registry} — rebuilding.",
                    registryId);
            }
        }

        try
        {
            var spec = MapToSpec(template);
            var contextDir = Path.Combine(Path.GetTempPath(), $"andy-orchestrator-{Guid.NewGuid():N}");
            Directory.CreateDirectory(contextDir);
            var context = new EmptyBuildContext(contextDir);

            try
            {
                var artifact = await _backend.BuildAsync(spec, context, progress, ct);

                var repoPath = template.Code;
                var tag = ToTagFromHash(template.SpecHash ?? artifact.SpecHash);
                var reference = await adapter.PushAsync(artifact, repoPath, tag, ct);

                var entity = new BuildArtifactEntity
                {
                    Id = Guid.NewGuid(),
                    Digest = reference.Digest,
                    MediaType = artifact.MediaType,
                    SizeBytes = artifact.SizeBytes,
                    SpecHash = artifact.SpecHash,
                    TemplateId = template.Id,
                    BuildBackendId = _backend.BackendId,
                    BuiltBy = request.RequestedBy,
                    BuiltAt = DateTime.UtcNow,
                };
                await _store.AddAsync(entity, ct);

                // Force-rebuilds and re-registers can produce the same
                // (registryId, repoPath, tag) coordinate as an existing
                // reference (typically when SpecHash is unchanged but
                // the build was re-run). Tags are mutable in OCI; the
                // registry happily overwrites — so we mirror that by
                // updating the existing row to point at the new digest
                // rather than letting the composite unique constraint
                // fire.
                var existingRef = await _db.RegistryReferences
                    .FirstOrDefaultAsync(r =>
                        r.RegistryId == reference.RegistryId &&
                        r.RepoPath == reference.RepoPath &&
                        r.Tag == reference.Tag, ct);
                if (existingRef is not null)
                {
                    existingRef.BuildArtifactId = entity.Id;
                    existingRef.PushedAt = reference.PushedAt.UtcDateTime;
                    existingRef.PushedBy = string.IsNullOrEmpty(reference.PushedBy) ? request.RequestedBy : reference.PushedBy;
                    await _db.SaveChangesAsync(ct);
                }
                else
                {
                    var refEntity = new RegistryReferenceEntity
                    {
                        Id = reference.Id,
                        RegistryId = reference.RegistryId,
                        RepoPath = reference.RepoPath,
                        Tag = reference.Tag,
                        PushedAt = reference.PushedAt.UtcDateTime,
                        PushedBy = string.IsNullOrEmpty(reference.PushedBy) ? request.RequestedBy : reference.PushedBy,
                    };
                    await _store.AddReferenceAsync(entity.Id, refEntity, ct);
                }

                return new BuildResult
                {
                    BuildId = buildId,
                    Status = BuildResultStatus.Succeeded,
                    Digest = reference.Digest,
                    References = [
                        new BuildResultReference(
                            reference.RegistryId,
                            reference.RepoPath,
                            reference.Tag,
                            reference.PushedAt),
                    ],
                };
            }
            finally
            {
                try { Directory.Delete(contextDir, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        }
        catch (ImageBuildFailedException ex)
        {
            return new BuildResult
            {
                BuildId = buildId,
                Status = BuildResultStatus.Failed,
                ErrorCode = $"build.{ex.FailingStepName ?? "failed"}",
                ErrorMessage = ex.Message,
                FailureLog = ex.CapturedLogs,
            };
        }
        catch (RegistryUploadException ex)
        {
            return new BuildResult
            {
                BuildId = buildId,
                Status = BuildResultStatus.Failed,
                ErrorCode = ex.Code,
                ErrorMessage = ex.Message,
                FailureLog = ex.CapturedOutput,
            };
        }
    }

    private IRegistryAdapter? ResolveAdapter(string registryId)
        => _adapters.FirstOrDefault(a =>
            string.Equals(a.RegistryId, registryId, StringComparison.OrdinalIgnoreCase));

    private static TemplateSpec MapToSpec(ContainerTemplate t)
    {
        return new TemplateSpec(
            Code: t.Code,
            Version: t.Version,
            SpecHash: t.SpecHash ?? string.Empty,
            CanonicalJson: string.Empty)
        {
            BaseImage = t.BaseImage,
            Extends = t.Extends,
            EntryPoint = t.EntryPoint,
            Packages = ParseStringList(t.Packages),
            Files = ParseFiles(t.Files),
            Install = ParseStringList(t.Install),
            Markers = ParseMarkers(t.Markers),
        };
    }

    private static IReadOnlyList<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return [];
            }
            return doc.RootElement.EnumerateArray()
                .Select(e => e.GetString() ?? string.Empty)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<TemplateFile> ParseFiles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return [];
            }
            var files = new List<TemplateFile>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != System.Text.Json.JsonValueKind.Object) { continue; }
                var source = entry.TryGetProperty("source", out var s) ? s.GetString() ?? string.Empty : string.Empty;
                var dest = entry.TryGetProperty("dest", out var d) ? d.GetString() ?? string.Empty : string.Empty;
                int? mode = null;
                if (entry.TryGetProperty("mode", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    mode = m.GetInt32();
                }
                if (!string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(dest))
                {
                    files.Add(new TemplateFile(source, dest, mode));
                }
            }
            return files;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseMarkers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, IReadOnlyList<string>>();
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return new Dictionary<string, IReadOnlyList<string>>();
            }
            var markers = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    markers[prop.Name] = prop.Value.EnumerateArray()
                        .Select(e => e.GetString() ?? string.Empty)
                        .ToList();
                }
                else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    markers[prop.Name] = new List<string> { prop.Value.GetString() ?? string.Empty };
                }
            }
            return markers;
        }
        catch (System.Text.Json.JsonException)
        {
            return new Dictionary<string, IReadOnlyList<string>>();
        }
    }

    private static string ToTagFromHash(string specHash)
    {
        // OCI tags can't contain ':', so 'sha256:abc...' becomes
        // 'sha256-abc...'. Truncated to 12 hex chars after the
        // prefix to keep registry UIs scannable.
        const int shortHexLen = 12;
        if (string.IsNullOrEmpty(specHash))
        {
            return $"unhashed-{Guid.NewGuid():N}"[..32];
        }
        var idx = specHash.IndexOf(':');
        var algo = idx > 0 ? specHash[..idx] : "sha256";
        var hex = idx > 0 ? specHash[(idx + 1)..] : specHash;
        if (hex.Length > shortHexLen)
        {
            hex = hex[..shortHexLen];
        }
        return $"{algo}-{hex}";
    }

    /// <summary>
    /// Empty <see cref="IBuildContext"/> for orchestrator-managed builds.
    /// IM8 doesn't yet plumb multipart-uploaded files through the
    /// orchestrator (the JSON-only register endpoint doesn't accept
    /// them); files-on-disk staging will land alongside the multipart
    /// register variant.
    /// </summary>
    private sealed class EmptyBuildContext : IBuildContext
    {
        public EmptyBuildContext(string dir) { ContextDirectoryPath = dir; }
        public string ContextDirectoryPath { get; }
        public IReadOnlyList<UploadedFile> Files => Array.Empty<UploadedFile>();
    }
}
