using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public class ImageDiffService : IImageDiffService
{
    private readonly ContainersDbContext _db;
    private readonly IImageManifestService _manifestService;

    public ImageDiffService(ContainersDbContext db, IImageManifestService manifestService)
    {
        _db = db;
        _manifestService = manifestService;
    }

    public async Task<ImageDiffResponse> DiffAsync(Guid fromImageId, Guid toImageId, CancellationToken ct = default)
    {
        var fromImage = await _db.Images.FindAsync([fromImageId], ct)
            ?? throw new KeyNotFoundException($"Image {fromImageId} not found");
        var toImage = await _db.Images.FindAsync([toImageId], ct)
            ?? throw new KeyNotFoundException($"Image {toImageId} not found");

        var fromManifest = await _manifestService.GetManifestAsync(fromImageId, ct);
        var toManifest = await _manifestService.GetManifestAsync(toImageId, ct);

        // Handle missing manifests
        if (fromManifest is null || toManifest is null)
        {
            return new ImageDiffResponse(
                FromImageId: fromImageId,
                ToImageId: toImageId,
                BaseImageChanged: fromImage.BaseImageDigest != toImage.BaseImageDigest,
                OsVersionChanged: null,
                ArchitectureChanged: false,
                ToolChanges: [],
                PackageChanges: new PackageChangeSummary(0, 0, 0, 0),
                SizeChange: ComputeSizeChange(fromImage, toImage),
                Warning: "One or both images have not been introspected");
        }

        var toolChanges = ComputeToolChanges(fromManifest, toManifest);
        var packageChanges = ComputePackageChanges(fromManifest, toManifest);

        string? osVersionChanged = null;
        if (fromManifest.OperatingSystem.Version != toManifest.OperatingSystem.Version)
        {
            osVersionChanged = $"{fromManifest.OperatingSystem.Version} → {toManifest.OperatingSystem.Version}";
        }

        return new ImageDiffResponse(
            FromImageId: fromImageId,
            ToImageId: toImageId,
            BaseImageChanged: fromImage.BaseImageDigest != toImage.BaseImageDigest,
            OsVersionChanged: osVersionChanged,
            ArchitectureChanged: fromManifest.Architecture != toManifest.Architecture,
            ToolChanges: toolChanges,
            PackageChanges: packageChanges,
            SizeChange: ComputeSizeChange(fromImage, toImage));
    }

    private static List<ToolChangeDto> ComputeToolChanges(ImageToolManifest from, ImageToolManifest to)
    {
        var changes = new List<ToolChangeDto>();

        var fromTools = from.Tools.ToDictionary(t => t.Name, t => t, StringComparer.OrdinalIgnoreCase);
        var toTools = to.Tools.ToDictionary(t => t.Name, t => t, StringComparer.OrdinalIgnoreCase);

        // Check tools in "to" — added or changed
        foreach (var (name, toTool) in toTools)
        {
            if (!fromTools.TryGetValue(name, out var fromTool))
            {
                changes.Add(new ToolChangeDto(
                    name, toTool.Type.ToString(),
                    DependencyChangeType.Added.ToString(),
                    null, toTool.Version, null));
            }
            else if (fromTool.Version != toTool.Version)
            {
                var severity = VersionConstraintMatcher.ClassifyChange(fromTool.Version, toTool.Version);
                changes.Add(new ToolChangeDto(
                    name, toTool.Type.ToString(),
                    DependencyChangeType.VersionChanged.ToString(),
                    fromTool.Version, toTool.Version,
                    severity.ToString()));
            }
        }

        // Check tools in "from" but not in "to" — removed
        foreach (var (name, fromTool) in fromTools)
        {
            if (!toTools.ContainsKey(name))
            {
                changes.Add(new ToolChangeDto(
                    name, fromTool.Type.ToString(),
                    DependencyChangeType.Removed.ToString(),
                    fromTool.Version, null, null));
            }
        }

        return changes.OrderBy(c => c.Name).ToList();
    }

    private static PackageChangeSummary ComputePackageChanges(ImageToolManifest from, ImageToolManifest to)
    {
        var fromPkgs = from.OsPackages.ToDictionary(p => p.Name, p => p.Version, StringComparer.OrdinalIgnoreCase);
        var toPkgs = to.OsPackages.ToDictionary(p => p.Name, p => p.Version, StringComparer.OrdinalIgnoreCase);

        int added = 0, removed = 0, upgraded = 0, downgraded = 0;

        foreach (var (name, toVersion) in toPkgs)
        {
            if (!fromPkgs.TryGetValue(name, out var fromVersion))
            {
                added++;
            }
            else if (fromVersion != toVersion)
            {
                if (string.Compare(toVersion, fromVersion, StringComparison.Ordinal) > 0)
                    upgraded++;
                else
                    downgraded++;
            }
        }

        foreach (var name in fromPkgs.Keys)
        {
            if (!toPkgs.ContainsKey(name))
                removed++;
        }

        return new PackageChangeSummary(added, removed, upgraded, downgraded);
    }

    private static string? ComputeSizeChange(ContainerImage from, ContainerImage to)
    {
        if (from.ImageSizeBytes is null || to.ImageSizeBytes is null)
            return null;

        var diff = to.ImageSizeBytes.Value - from.ImageSizeBytes.Value;
        var absMb = Math.Abs(diff) / (1024.0 * 1024.0);
        return diff >= 0 ? $"+{absMb:F0}MB" : $"-{absMb:F0}MB";
    }
}
