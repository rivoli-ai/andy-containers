using System.Text.Json;
using System.Text.RegularExpressions;
using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Parses version command output for various tools. All methods return null on parse failure.
/// </summary>
public static partial class VersionParser
{
    /// <summary>Parses "8.0.404 [/usr/share/dotnet/sdk]" → "8.0.404"</summary>
    public static string? ParseDotnetSdk(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        // Take first line, extract version before the space/bracket
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (line is null) return null;
        var match = VersionAtStartRegex().Match(line.Trim());
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Parses "Microsoft.NETCore.App 8.0.11 [/usr/share/dotnet/shared/...]" → "8.0.11"</summary>
    public static string? ParseDotnetRuntime(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("NETCore.App", StringComparison.OrdinalIgnoreCase)) continue;
            var match = VersionInLineRegex().Match(line);
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    /// <summary>Parses "Python 3.12.8" → "3.12.8"</summary>
    public static string? ParsePython(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = PythonVersionRegex().Match(output.Trim());
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Parses "v20.18.1" → "20.18.1"</summary>
    public static string? ParseNode(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var line = output.Trim().Split('\n')[0].Trim();
        return line.StartsWith('v') ? line[1..] : (VersionAtStartRegex().IsMatch(line) ? line : null);
    }

    /// <summary>Parses "10.8.2\n" → "10.8.2"</summary>
    public static string? ParseNpm(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var line = output.Trim().Split('\n')[0].Trim();
        return VersionAtStartRegex().IsMatch(line) ? VersionAtStartRegex().Match(line).Groups[1].Value : null;
    }

    /// <summary>Parses "go version go1.22.5 linux/amd64" → "1.22.5"</summary>
    public static string? ParseGo(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = GoVersionRegex().Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Parses "rustc 1.82.0 (f6e511e..." → "1.82.0"</summary>
    public static string? ParseRust(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = RustVersionRegex().Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Parses "openjdk 21.0.4 2024-07-16 LTS" → "21.0.4"</summary>
    public static string? ParseJava(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        // java --version outputs to stderr; first line has version
        var line = output.Trim().Split('\n')[0];
        var match = VersionInLineRegex().Match(line);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Parses "git version 2.43.0" → "2.43.0"</summary>
    public static string? ParseGit(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = GitVersionRegex().Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Parses "Docker version 27.3.1, build ..." → "27.3.1"</summary>
    public static string? ParseDocker(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = DockerVersionRegex().Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Parses kubectl JSON output → version from clientVersion.gitVersion</summary>
    public static string? ParseKubectl(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("clientVersion", out var cv) &&
                cv.TryGetProperty("gitVersion", out var gv))
            {
                var version = gv.GetString();
                if (version is not null && version.StartsWith('v'))
                    return version[1..];
                return version;
            }
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>Parses "4.96.2\n..." → "4.96.2" (first line)</summary>
    public static string? ParseCodeServer(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var line = output.Trim().Split('\n')[0].Trim();
        return VersionAtStartRegex().IsMatch(line) ? VersionAtStartRegex().Match(line).Groups[1].Value : null;
    }

    /// <summary>Parses "OpenSSH_9.6p1 Ubuntu-3ubuntu13.5" → "9.6p1" (from stderr)</summary>
    public static string? ParseOpenSsh(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = OpenSshVersionRegex().Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Parses "curl 8.5.0 (x86_64-pc-linux-gnu)" → "8.5.0"</summary>
    public static string? ParseCurl(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = CurlVersionRegex().Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Parses ng version output containing "Angular CLI: 18.2.12" → "18.2.12"</summary>
    public static string? ParseAngularCli(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = AngularCliVersionRegex().Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Parses dpkg-query output (tab-separated: name, version, arch) into packages.
    /// Expected format per line: "package\tversion\tarchitecture"
    /// </summary>
    public static List<InstalledPackage> ParseDpkgQuery(string? output)
    {
        var packages = new List<InstalledPackage>();
        if (string.IsNullOrWhiteSpace(output)) return packages;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length >= 2)
            {
                packages.Add(new InstalledPackage
                {
                    Name = parts[0].Trim(),
                    Version = parts[1].Trim(),
                    Architecture = parts.Length >= 3 ? parts[2].Trim() : null
                });
            }
        }

        return packages;
    }

    /// <summary>
    /// Parses /etc/os-release format into OsInfo.
    /// </summary>
    public static OsInfo? ParseOsRelease(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"');
            fields[key] = value;
        }

        return new OsInfo
        {
            Name = fields.GetValueOrDefault("NAME", "Unknown"),
            Version = fields.GetValueOrDefault("VERSION_ID", "Unknown"),
            Codename = fields.GetValueOrDefault("VERSION_CODENAME", "Unknown"),
            KernelVersion = "" // Populated separately from uname -r
        };
    }

    // --- Generated Regexes ---

    [GeneratedRegex(@"^(\d+\.\d+[\.\d]*)")]
    private static partial Regex VersionAtStartRegex();

    [GeneratedRegex(@"(\d+\.\d+\.\d+)")]
    private static partial Regex VersionInLineRegex();

    [GeneratedRegex(@"Python\s+(\d+\.\d+\.\d+)")]
    private static partial Regex PythonVersionRegex();

    [GeneratedRegex(@"go(\d+\.\d+[\.\d]*)")]
    private static partial Regex GoVersionRegex();

    [GeneratedRegex(@"rustc\s+(\d+\.\d+\.\d+)")]
    private static partial Regex RustVersionRegex();

    [GeneratedRegex(@"git version\s+(\d+\.\d+\.\d+)")]
    private static partial Regex GitVersionRegex();

    [GeneratedRegex(@"Docker version\s+(\d+\.\d+[\.\d]*)")]
    private static partial Regex DockerVersionRegex();

    [GeneratedRegex(@"OpenSSH[_\s](\S+)")]
    private static partial Regex OpenSshVersionRegex();

    [GeneratedRegex(@"curl\s+(\d+\.\d+[\.\d]*)")]
    private static partial Regex CurlVersionRegex();

    [GeneratedRegex(@"Angular CLI:\s*(\d+\.\d+[\.\d]*)")]
    private static partial Regex AngularCliVersionRegex();
}
