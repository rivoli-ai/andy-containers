using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public partial class ToolVersionDetector : IToolVersionDetector
{
    public string GenerateIntrospectionScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine("echo '{'");
        sb.AppendLine();

        // OS info
        sb.AppendLine("echo '\"os\": {'");
        sb.AppendLine("echo '\"name\": \"'$(. /etc/os-release 2>/dev/null && echo $NAME || echo unknown)'\",'");
        sb.AppendLine("echo '\"version\": \"'$(. /etc/os-release 2>/dev/null && echo $VERSION_ID || echo unknown)'\",'");
        sb.AppendLine("echo '\"codename\": \"'$(. /etc/os-release 2>/dev/null && echo $VERSION_CODENAME || echo unknown)'\",'");
        sb.AppendLine("echo '\"kernel\": \"'$(uname -r)'\"'");
        sb.AppendLine("echo '},'");
        sb.AppendLine("echo '\"arch\": \"'$(uname -m)'\",'");
        sb.AppendLine();

        // Tools
        sb.AppendLine("echo '\"tools\": ['");
        var tools = new (string name, string check, string versionCmd, string type)[]
        {
            ("dotnet-sdk", "dotnet", "dotnet --version 2>/dev/null", "Sdk"),
            ("python", "python3", "python3 -c \"import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}')\" 2>/dev/null", "Runtime"),
            ("node", "node", "node --version 2>/dev/null | sed 's/^v//'", "Runtime"),
            ("npm", "npm", "npm --version 2>/dev/null", "Tool"),
            ("go", "go", "go version 2>/dev/null | grep -oP 'go\\K[0-9]+\\.[0-9]+\\.[0-9]+'", "Runtime"),
            ("rustc", "rustc", "rustc --version 2>/dev/null | awk '{print $2}'", "Compiler"),
            ("java", "java", "java --version 2>/dev/null | head -1 | awk '{print $2}'", "Runtime"),
            ("git", "git", "git --version 2>/dev/null | awk '{print $3}'", "Tool"),
            ("curl", "curl", "curl --version 2>/dev/null | head -1 | awk '{print $2}'", "Tool"),
            ("code-server", "code-server", "code-server --version 2>/dev/null | head -1", "Tool"),
        };

        for (int i = 0; i < tools.Length; i++)
        {
            var (name, check, cmd, type) = tools[i];
            var comma = i < tools.Length - 1 ? "," : "";
            sb.AppendLine($"if command -v {check} &>/dev/null; then");
            sb.AppendLine($"  echo '{{\"name\":\"{name}\",\"version\":\"'$({cmd})'\",\"type\":\"{type}\",\"path\":\"'$(which {check})'\"}}{comma}'");
            sb.AppendLine("fi");
        }

        sb.AppendLine("echo ']'");
        sb.AppendLine("echo '}'");
        return sb.ToString();
    }

    public IReadOnlyList<InstalledTool> ParseIntrospectionOutput(string jsonOutput, IReadOnlyList<DependencySpec>? declaredDeps = null)
    {
        var tools = new List<InstalledTool>();
        try
        {
            using var doc = JsonDocument.Parse(jsonOutput);
            if (!doc.RootElement.TryGetProperty("tools", out var toolsArray)) return tools;

            foreach (var toolElement in toolsArray.EnumerateArray())
            {
                var name = toolElement.GetProperty("name").GetString() ?? "";
                var version = toolElement.GetProperty("version").GetString() ?? "";
                var typeStr = toolElement.TryGetProperty("type", out var t) ? t.GetString() : "Tool";
                var path = toolElement.TryGetProperty("path", out var p) ? p.GetString() : null;

                var depType = Enum.TryParse<DependencyType>(typeStr, ignoreCase: true, out var dt) ? dt : DependencyType.Tool;

                string? declaredVersion = null;
                bool matchesDeclared = false;

                if (declaredDeps is not null)
                {
                    var matching = declaredDeps.FirstOrDefault(d =>
                        d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (matching is not null)
                    {
                        declaredVersion = matching.VersionConstraint;
                        matchesDeclared = VersionComparer.SatisfiesConstraint(matching.VersionConstraint, version);
                    }
                }

                tools.Add(new InstalledTool
                {
                    Name = name,
                    Version = version,
                    Type = depType,
                    DeclaredVersion = declaredVersion,
                    MatchesDeclared = matchesDeclared,
                    BinaryPath = path
                });
            }
        }
        catch (JsonException)
        {
            // Return whatever we've parsed so far
        }

        return tools;
    }

    public OsInfo ParseOsInfo(string osReleaseContent, string unameArch, string unameKernel)
    {
        var info = new OsInfo { Name = "Unknown", Version = "Unknown" };

        foreach (var line in osReleaseContent.Split('\n'))
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2) continue;
            var key = parts[0].Trim();
            var value = parts[1].Trim().Trim('"');

            switch (key)
            {
                case "NAME": info.Name = value; break;
                case "VERSION_ID": info.Version = value; break;
                case "VERSION_CODENAME": info.Codename = value; break;
            }
        }

        info.KernelVersion = unameKernel.Trim();
        return info;
    }

    public IReadOnlyList<InstalledPackage> ParseDpkgOutput(string dpkgOutput)
    {
        var packages = new List<InstalledPackage>();
        foreach (var line in dpkgOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            packages.Add(new InstalledPackage
            {
                Name = parts[0],
                Version = parts[1],
                Architecture = parts.Length > 2 ? parts[2] : null
            });
        }
        return packages;
    }
}
