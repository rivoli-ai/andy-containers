using Andy.Containers.Abstractions;

namespace Andy.Containers.Api.Services;

public interface IImageDiffService
{
    Task<ImageDiffResponse> DiffAsync(Guid fromImageId, Guid toImageId, CancellationToken ct = default);
}

public record ImageDiffResponse(
    Guid FromImageId,
    Guid ToImageId,
    bool BaseImageChanged,
    string? OsVersionChanged,
    bool ArchitectureChanged,
    List<ToolChangeDto> ToolChanges,
    PackageChangeSummary PackageChanges,
    string? SizeChange,
    string? Warning = null);

public record ToolChangeDto(
    string Name,
    string Type,
    string ChangeType,
    string? PreviousVersion,
    string? NewVersion,
    string? Severity);

public record PackageChangeSummary(
    int Added,
    int Removed,
    int Upgraded,
    int Downgraded);
