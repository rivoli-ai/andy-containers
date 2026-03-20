using System.Text.Json;
using Andy.Containers.Api.Telemetry;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

public class ImageManifestService : IImageManifestService
{
    private readonly ContainersDbContext _db;
    private readonly IToolVersionDetector _detector;
    private readonly ILogger<ImageManifestService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ImageManifestService(
        ContainersDbContext db,
        IToolVersionDetector detector,
        ILogger<ImageManifestService> logger)
    {
        _db = db;
        _detector = detector;
        _logger = logger;
    }

    public async Task<(ImageToolManifest Manifest, ContainerImage Image)> GenerateManifestAsync(
        Guid imageId, CancellationToken ct = default)
    {
        using var activity = ActivitySources.Introspection.StartActivity("ResolveImageManifest");
        activity?.SetTag("imageId", imageId.ToString());

        var image = await _db.Images
            .Include(i => i.Template)
            .FirstOrDefaultAsync(i => i.Id == imageId, ct)
            ?? throw new KeyNotFoundException($"Image {imageId} not found");

        // Load declared dependencies for the template
        var declaredDeps = image.Template is not null
            ? await _db.DependencySpecs
                .Where(d => d.TemplateId == image.TemplateId)
                .ToListAsync(ct)
            : new List<DependencySpec>();

        // Run introspection
        var manifest = await _detector.IntrospectImageAsync(
            image.ImageReference, declaredDeps, ct);

        // Fill in image-specific fields
        manifest.BaseImage = image.Template?.BaseImage ?? "unknown";
        manifest.BaseImageDigest = image.BaseImageDigest;

        // Compute content hash
        var contentHash = ContentHashCalculator.ComputeHash(manifest);
        manifest.ImageContentHash = contentHash;

        // Deduplication: check if an image with this hash already exists
        var existing = await _db.Images
            .FirstOrDefaultAsync(i => i.ContentHash == contentHash && i.Id != imageId, ct);

        if (existing is not null)
        {
            _logger.LogInformation(
                "Deduplication: image {ImageId} has same content hash as {ExistingId}, returning existing",
                imageId, existing.Id);

            var existingManifest = DeserializeManifest(existing.DependencyManifest);
            if (existingManifest is not null)
                return (existingManifest, existing);
        }

        // Persist manifest
        image.ContentHash = contentHash;
        image.DependencyManifest = JsonSerializer.Serialize(manifest, JsonOptions);
        image.DependencyLock = GenerateDependencyLock(manifest);

        // Create ResolvedDependency records
        await CreateResolvedDependencies(image, manifest, declaredDeps, ct);

        await _db.SaveChangesAsync(ct);

        return (manifest, image);
    }

    public async Task<ImageToolManifest?> GetManifestAsync(Guid imageId, CancellationToken ct = default)
    {
        var image = await _db.Images.FindAsync([imageId], ct);
        if (image is null) return null;

        return DeserializeManifest(image.DependencyManifest);
    }

    public async Task<(ImageToolManifest Manifest, ContainerImage Image)> RefreshManifestAsync(
        Guid imageId, CancellationToken ct = default)
    {
        // Remove existing resolved dependencies
        var existingDeps = await _db.ResolvedDependencies
            .Where(r => r.ImageId == imageId)
            .ToListAsync(ct);
        _db.ResolvedDependencies.RemoveRange(existingDeps);

        return await GenerateManifestAsync(imageId, ct);
    }

    private async Task CreateResolvedDependencies(
        ContainerImage image,
        ImageToolManifest manifest,
        List<DependencySpec> declaredDeps,
        CancellationToken ct)
    {
        // Remove any existing resolved dependencies for this image
        var existing = await _db.ResolvedDependencies
            .Where(r => r.ImageId == image.Id)
            .ToListAsync(ct);
        _db.ResolvedDependencies.RemoveRange(existing);

        var specsByName = declaredDeps
            .ToDictionary(d => d.Name, d => d, StringComparer.OrdinalIgnoreCase);

        foreach (var tool in manifest.Tools)
        {
            // Try to find a matching DependencySpec
            specsByName.TryGetValue(tool.Name, out var spec);

            var resolved = new ResolvedDependency
            {
                ImageId = image.Id,
                DependencySpecId = spec?.Id ?? Guid.Empty,
                ResolvedVersion = tool.Version,
                Source = "detected-via-introspection"
            };

            // Only link to spec if we found one
            if (spec is not null)
                resolved.DependencySpecId = spec.Id;

            _db.ResolvedDependencies.Add(resolved);
        }
    }

    private static string GenerateDependencyLock(ImageToolManifest manifest)
    {
        var lockFile = new
        {
            lockVersion = 1,
            generatedAt = DateTime.UtcNow,
            dependencies = manifest.Tools.Select(t => new
            {
                name = t.Name,
                version = t.Version,
                type = t.Type.ToString(),
                declaredConstraint = t.DeclaredVersion,
                satisfiesConstraint = t.MatchesDeclared,
                source = "detected-via-introspection",
                artifactHash = (string?)null
            }).ToList()
        };

        return JsonSerializer.Serialize(lockFile, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private static ImageToolManifest? DeserializeManifest(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return null;

        try
        {
            return JsonSerializer.Deserialize<ImageToolManifest>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
