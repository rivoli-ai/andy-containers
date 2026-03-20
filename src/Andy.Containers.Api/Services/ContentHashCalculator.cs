using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Computes content-addressed hashes for image manifests.
/// Hash is deterministic: identical tool sets produce identical hashes.
/// </summary>
public static class ContentHashCalculator
{
    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Computes a SHA-256 content hash from the manifest's tools, base image, and architecture.
    /// OS packages are intentionally excluded (too volatile — security patches shouldn't change identity).
    /// </summary>
    public static string ComputeHash(ImageToolManifest manifest)
    {
        // Build canonical input: sorted tools + base image + architecture + OS
        var hashInput = new ContentHashInput
        {
            BaseImageDigest = manifest.BaseImageDigest,
            Architecture = manifest.Architecture,
            OsName = manifest.OperatingSystem.Name,
            OsVersion = manifest.OperatingSystem.Version,
            Tools = manifest.Tools
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .Select(t => new ToolHashEntry { Name = t.Name, Version = t.Version })
                .ToList()
        };

        var json = JsonSerializer.Serialize(hashInput, CanonicalOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();

        return $"sha256:{hex}";
    }

    private record ContentHashInput
    {
        public required string BaseImageDigest { get; init; }
        public required string Architecture { get; init; }
        public required string OsName { get; init; }
        public required string OsVersion { get; init; }
        public required List<ToolHashEntry> Tools { get; init; }
    }

    private record ToolHashEntry
    {
        public required string Name { get; init; }
        public required string Version { get; init; }
    }
}
