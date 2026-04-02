using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Services;

public interface ITemplateBuildService
{
    Task<ImageBuildRecord?> GetBuildStatusAsync(string templateCode, CancellationToken ct);
    Task<IReadOnlyList<ImageBuildRecord>> GetAllBuildStatusesAsync(CancellationToken ct);
    Task<ImageBuildRecord> TriggerBuildAsync(string templateCode, CancellationToken ct);
    Task RefreshStatusesAsync(CancellationToken ct);
}

public class TemplateBuildService : ITemplateBuildService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TemplateBuildService> _logger;
    private readonly IWebHostEnvironment _env;
    private static readonly SemaphoreSlim BuildLock = new(1, 1);

    public TemplateBuildService(IServiceScopeFactory scopeFactory, ILogger<TemplateBuildService> logger, IWebHostEnvironment env)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _env = env;
    }

    public async Task<ImageBuildRecord?> GetBuildStatusAsync(string templateCode, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();
        return await db.ImageBuildRecords.FirstOrDefaultAsync(r => r.TemplateCode == templateCode, ct);
    }

    public async Task<IReadOnlyList<ImageBuildRecord>> GetAllBuildStatusesAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();
        return await db.ImageBuildRecords.ToListAsync(ct);
    }

    public async Task<ImageBuildRecord> TriggerBuildAsync(string templateCode, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();

        var template = await db.Templates.FirstOrDefaultAsync(t => t.Code == templateCode, ct)
            ?? throw new ArgumentException($"Template '{templateCode}' not found");

        if (!template.BaseImage.StartsWith("andy-"))
            throw new InvalidOperationException("Only custom andy-* images can be built");

        var record = await db.ImageBuildRecords.FirstOrDefaultAsync(r => r.TemplateCode == templateCode, ct);
        if (record is null)
        {
            record = new ImageBuildRecord
            {
                ImageReference = template.BaseImage,
                TemplateCode = templateCode,
                Status = TemplateBuildStatus.Building
            };
            db.ImageBuildRecords.Add(record);
        }
        else
        {
            record.Status = TemplateBuildStatus.Building;
            record.LastBuildError = null;
            record.BuildLog = null;
        }
        await db.SaveChangesAsync(ct);

        // Build in background
        var recordId = record.Id;
        var baseImage = template.BaseImage;
        _ = Task.Run(async () => await BuildImageAsync(recordId, baseImage, templateCode), CancellationToken.None);

        return record;
    }

    private async Task BuildImageAsync(Guid recordId, string imageReference, string templateCode)
    {
        await BuildLock.WaitAsync();
        try
        {
            var (buildDir, scriptsDir) = FindBuildDirectory(imageReference);
            if (buildDir is null)
            {
                await UpdateRecordAsync(recordId, TemplateBuildStatus.Failed,
                    error: $"Build directory not found for {imageReference}");
                return;
            }

            _logger.LogInformation("Building image {Image} from {Dir}", imageReference, buildDir);

            var args = $"buildx build -t {imageReference}";
            if (scriptsDir is not null && Directory.Exists(scriptsDir))
                args += $" --build-context scripts={scriptsDir}";
            args += $" {buildDir}";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var log = string.IsNullOrEmpty(stdout) ? stderr : stdout + "\n" + stderr;
            // Truncate log to 50KB
            if (log.Length > 50_000)
                log = log[^50_000..];

            if (process.ExitCode == 0)
            {
                var checksum = ComputeDockerfileChecksum(buildDir);
                var imageInfo = await InspectImageAsync(imageReference);
                await UpdateRecordAsync(recordId, TemplateBuildStatus.Built,
                    builtAt: DateTime.UtcNow, checksum: checksum, log: log, imageInfo: imageInfo);
                _logger.LogInformation("Image {Image} built successfully ({Size} bytes, {Layers} layers)",
                    imageReference, imageInfo?.SizeBytes ?? 0, imageInfo?.LayerCount ?? 0);
            }
            else
            {
                await UpdateRecordAsync(recordId, TemplateBuildStatus.Failed,
                    error: stderr[..Math.Min(500, stderr.Length)], log: log);
                _logger.LogError("Image {Image} build failed: {Error}", imageReference, stderr[..Math.Min(200, stderr.Length)]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Image build failed for {RecordId}", recordId);
            await UpdateRecordAsync(recordId, TemplateBuildStatus.Failed, error: ex.Message);
        }
        finally
        {
            BuildLock.Release();
        }
    }

    public async Task RefreshStatusesAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();

        // Track all templates — custom andy-* images get build status, public images get metadata only
        var templates = await db.Templates.ToListAsync(ct);

        foreach (var template in templates)
        {
            var record = await db.ImageBuildRecords
                .FirstOrDefaultAsync(r => r.TemplateCode == template.Code, ct);

            var isCustomImage = template.BaseImage.StartsWith("andy-");
            var imageExists = await CheckImageExistsAsync(template.BaseImage);

            // Only compute Dockerfile checksum for custom images
            string? currentChecksum = null;
            if (isCustomImage)
            {
                var (buildDir, _) = FindBuildDirectory(template.BaseImage);
                currentChecksum = buildDir is not null ? ComputeDockerfileChecksum(buildDir) : null;
            }

            // Capture image info if image exists locally
            ImageInspectInfo? imageInfo = null;
            if (imageExists)
                imageInfo = await InspectImageAsync(template.BaseImage);

            if (record is null)
            {
                record = new ImageBuildRecord
                {
                    ImageReference = template.BaseImage,
                    TemplateCode = template.Code,
                    CheckedAt = DateTime.UtcNow,
                    DockerfileChecksum = currentChecksum
                };

                if (isCustomImage)
                {
                    record.Status = imageExists ? TemplateBuildStatus.Built : TemplateBuildStatus.NotBuilt;
                    if (imageExists && currentChecksum is not null)
                        record.LastBuiltAt = DateTime.UtcNow; // Approximate
                }
                else
                {
                    // Public images: Built if pulled locally, NotBuilt otherwise
                    record.Status = imageExists ? TemplateBuildStatus.Built : TemplateBuildStatus.NotBuilt;
                }

                if (imageInfo is not null)
                {
                    record.ImageSizeBytes = imageInfo.SizeBytes;
                    record.Architecture = imageInfo.Architecture;
                    record.Os = imageInfo.Os;
                    record.LayerCount = imageInfo.LayerCount;
                    record.ImageDigest = imageInfo.Digest;
                    record.ImageCreatedAt = imageInfo.CreatedAt;
                }

                db.ImageBuildRecords.Add(record);
            }
            else
            {
                record.CheckedAt = DateTime.UtcNow;
                if (!imageExists)
                {
                    record.Status = TemplateBuildStatus.NotBuilt;
                    record.ImageSizeBytes = null;
                    record.Architecture = null;
                    record.Os = null;
                    record.LayerCount = null;
                    record.ImageDigest = null;
                    record.ImageCreatedAt = null;
                }
                else if (isCustomImage && record.Status == TemplateBuildStatus.Built &&
                         currentChecksum is not null &&
                         record.DockerfileChecksum is not null &&
                         currentChecksum != record.DockerfileChecksum)
                {
                    record.Status = TemplateBuildStatus.Outdated;
                }
                else if (record.Status is TemplateBuildStatus.Unknown or TemplateBuildStatus.NotBuilt && imageExists)
                {
                    record.Status = TemplateBuildStatus.Built;
                }

                // Always update image metadata if available
                if (imageInfo is not null)
                {
                    record.ImageSizeBytes = imageInfo.SizeBytes;
                    record.Architecture = imageInfo.Architecture;
                    record.Os = imageInfo.Os;
                    record.LayerCount = imageInfo.LayerCount;
                    record.ImageDigest = imageInfo.Digest;
                    record.ImageCreatedAt = imageInfo.CreatedAt;
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task UpdateRecordAsync(Guid recordId, TemplateBuildStatus status,
        string? error = null, DateTime? builtAt = null, string? checksum = null, string? log = null,
        ImageInspectInfo? imageInfo = null)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();

        var record = await db.ImageBuildRecords.FindAsync(recordId);
        if (record is null) return;

        record.Status = status;
        record.CheckedAt = DateTime.UtcNow;
        if (error is not null) record.LastBuildError = error;
        if (builtAt.HasValue) record.LastBuiltAt = builtAt.Value;
        if (checksum is not null) record.DockerfileChecksum = checksum;
        if (log is not null) record.BuildLog = log;

        if (imageInfo is not null)
        {
            record.ImageSizeBytes = imageInfo.SizeBytes;
            record.Architecture = imageInfo.Architecture;
            record.Os = imageInfo.Os;
            record.LayerCount = imageInfo.LayerCount;
            record.ImageDigest = imageInfo.Digest;
            record.ImageCreatedAt = imageInfo.CreatedAt;
        }

        await db.SaveChangesAsync();
    }

    private record ImageInspectInfo(long SizeBytes, string? Architecture, string? Os, int LayerCount, string? Digest, DateTime? CreatedAt);

    private static async Task<ImageInspectInfo?> InspectImageAsync(string imageReference)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"image inspect {imageReference} --format json",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return null;

            using var doc = JsonDocument.Parse(output);
            // docker image inspect returns an array
            var root = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement[0]
                : doc.RootElement;

            var size = root.TryGetProperty("Size", out var sizeProp) ? sizeProp.GetInt64() : 0L;
            var arch = root.TryGetProperty("Architecture", out var archProp) ? archProp.GetString() : null;
            var os = root.TryGetProperty("Os", out var osProp) ? osProp.GetString() : null;
            var digest = root.TryGetProperty("RepoDigests", out var digestsProp) && digestsProp.GetArrayLength() > 0
                ? digestsProp[0].GetString() : null;

            var layerCount = 0;
            if (root.TryGetProperty("RootFS", out var rootFs) && rootFs.TryGetProperty("Layers", out var layers))
                layerCount = layers.GetArrayLength();

            DateTime? createdAt = null;
            if (root.TryGetProperty("Created", out var createdProp))
            {
                if (DateTime.TryParse(createdProp.GetString(), out var dt))
                    createdAt = dt.ToUniversalTime();
            }

            return new ImageInspectInfo(size, arch, os, layerCount, digest, createdAt);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> CheckImageExistsAsync(string imageReference)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"image inspect {imageReference}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            process.Start();
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private (string? buildDir, string? scriptsDir) FindBuildDirectory(string imageReference)
    {
        var imageName = imageReference.Replace(":latest", "").Replace("andy-", "");

        // Search upward from content root for images/ directory
        string? buildDir = null;
        var dir = new DirectoryInfo(_env.ContentRootPath);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "images", imageName);
            if (Directory.Exists(candidate)) { buildDir = candidate; break; }
            dir = dir.Parent;
        }

        if (buildDir is null) return (null, null);

        var scriptsDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(buildDir)!, "..", "scripts", "container"));
        return (buildDir, Directory.Exists(scriptsDir) ? scriptsDir : null);
    }

    private static string? ComputeDockerfileChecksum(string buildDir)
    {
        var dockerfilePath = Path.Combine(buildDir, "Dockerfile");
        if (!File.Exists(dockerfilePath)) return null;

        var content = File.ReadAllBytes(dockerfilePath);
        var hash = SHA256.HashData(content);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
