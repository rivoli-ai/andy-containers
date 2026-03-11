using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Andy.Containers.Api.Services;

public static class ContentHashCalculator
{
    public static string ComputeHash(IReadOnlyList<(string Name, string Version)> tools, string baseImageDigest, string architecture)
    {
        var sortedTools = tools.OrderBy(t => t.Name).Select(t => new { name = t.Name, version = t.Version }).ToArray();
        var input = new { tools = sortedTools, baseImageDigest, architecture };
        var json = JsonSerializer.Serialize(input, new JsonSerializerOptions { WriteIndented = false });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return $"sha256:{BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()}";
    }
}
