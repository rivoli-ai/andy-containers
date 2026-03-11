using System.Text.Json;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Services;

public class ImageManifestService : IImageManifestService
{
    private readonly ContainersDbContext _db;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ImageManifestService(ContainersDbContext db)
    {
        _db = db;
    }

    public async Task<ImageToolManifest?> GetManifestAsync(Guid imageId, CancellationToken ct = default)
    {
        var image = await _db.Images.FindAsync([imageId], ct);
        if (image is null) return null;

        if (string.IsNullOrEmpty(image.DependencyManifest) || image.DependencyManifest == "{}")
            return null;

        try
        {
            return JsonSerializer.Deserialize<ImageToolManifest>(image.DependencyManifest, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<ImageToolManifest> StoreManifestAsync(Guid imageId, ImageToolManifest manifest, CancellationToken ct = default)
    {
        var image = await _db.Images.FindAsync([imageId], ct)
            ?? throw new KeyNotFoundException($"Image {imageId} not found");

        image.DependencyManifest = JsonSerializer.Serialize(manifest, JsonOptions);

        // Update content hash based on actual tools
        var toolPairs = manifest.Tools.Select(t => (t.Name, t.Version)).ToList();
        image.ContentHash = ContentHashCalculator.ComputeHash(toolPairs, manifest.BaseImageDigest, manifest.Architecture);

        await _db.SaveChangesAsync(ct);
        return manifest;
    }

    public async Task<ImageDiffResult> DiffImagesAsync(Guid fromImageId, Guid toImageId, CancellationToken ct = default)
    {
        var fromManifest = await GetManifestAsync(fromImageId, ct);
        var toManifest = await GetManifestAsync(toImageId, ct);

        var fromImage = await _db.Images.FindAsync([fromImageId], ct);
        var toImage = await _db.Images.FindAsync([toImageId], ct);

        var result = new ImageDiffResult
        {
            FromImageId = fromImageId,
            ToImageId = toImageId
        };

        if (fromImage is not null && toImage is not null)
        {
            result.BaseImageChanged = fromImage.BaseImageDigest != toImage.BaseImageDigest;
            result.SizeChange = FormatSizeChange(fromImage.ImageSizeBytes, toImage.ImageSizeBytes);
        }

        var fromTools = fromManifest?.Tools.ToDictionary(t => t.Name) ?? new();
        var toTools = toManifest?.Tools.ToDictionary(t => t.Name) ?? new();

        if (fromManifest is not null && toManifest is not null)
        {
            result.ArchitectureChanged = fromManifest.Architecture != toManifest.Architecture;
            if (fromManifest.OperatingSystem.Version != toManifest.OperatingSystem.Version)
                result.OsVersionChanged = $"{fromManifest.OperatingSystem.Version} → {toManifest.OperatingSystem.Version}";
        }

        var changes = new List<ToolChange>();
        var allNames = fromTools.Keys.Union(toTools.Keys);

        foreach (var name in allNames)
        {
            var inFrom = fromTools.TryGetValue(name, out var fromTool);
            var inTo = toTools.TryGetValue(name, out var toTool);

            if (inFrom && inTo)
            {
                if (fromTool!.Version != toTool!.Version)
                {
                    changes.Add(new ToolChange
                    {
                        Name = name,
                        Type = toTool.Type,
                        ChangeType = "VersionChanged",
                        PreviousVersion = fromTool.Version,
                        NewVersion = toTool.Version,
                        Severity = VersionComparer.ClassifySeverity(fromTool.Version, toTool.Version)
                    });
                }
            }
            else if (inTo && !inFrom)
            {
                changes.Add(new ToolChange
                {
                    Name = name,
                    Type = toTool!.Type,
                    ChangeType = "Added",
                    NewVersion = toTool.Version
                });
            }
            else if (inFrom && !inTo)
            {
                changes.Add(new ToolChange
                {
                    Name = name,
                    Type = fromTool!.Type,
                    ChangeType = "Removed",
                    PreviousVersion = fromTool.Version
                });
            }
        }

        result.ToolChanges = changes;
        return result;
    }

    public async Task<IReadOnlyList<ContainerImage>> FindImagesByToolAsync(string toolName, string? minVersion = null, CancellationToken ct = default)
    {
        var images = await _db.Images
            .Where(i => i.BuildStatus == ImageBuildStatus.Succeeded && i.DependencyManifest != "{}")
            .ToListAsync(ct);

        var matching = new List<ContainerImage>();
        foreach (var image in images)
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<ImageToolManifest>(image.DependencyManifest, JsonOptions);
                if (manifest is null) continue;

                var tool = manifest.Tools.FirstOrDefault(t =>
                    t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
                if (tool is null) continue;

                if (minVersion is not null && VersionComparer.Compare(tool.Version, minVersion) < 0)
                    continue;

                matching.Add(image);
            }
            catch (JsonException) { }
        }

        return matching;
    }

    private static string? FormatSizeChange(long? fromSize, long? toSize)
    {
        if (!fromSize.HasValue || !toSize.HasValue) return null;
        var diff = toSize.Value - fromSize.Value;
        var prefix = diff >= 0 ? "+" : "";
        var absDiff = Math.Abs(diff);
        if (absDiff > 1_073_741_824) return $"{prefix}{diff / 1_073_741_824.0:F1}GB";
        if (absDiff > 1_048_576) return $"{prefix}{diff / 1_048_576.0:F0}MB";
        if (absDiff > 1024) return $"{prefix}{diff / 1024.0:F0}KB";
        return $"{prefix}{diff}B";
    }
}
