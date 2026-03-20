namespace Andy.Containers.Api.Services;

/// <summary>
/// Generates a shell script that detects all known tool versions in a single execution.
/// Output is tab-separated lines parseable by ToolVersionDetector.
/// </summary>
public static class IntrospectionScriptBuilder
{
    /// <summary>
    /// Builds a shell script that outputs structured introspection data.
    /// Each tool outputs: TOOL\t{name}\t{output}
    /// OS info outputs: OS_RELEASE\t{content}
    /// Architecture outputs: ARCH\t{value}
    /// Kernel outputs: KERNEL\t{value}
    /// Packages output: PACKAGES\t{dpkg or apk output}
    /// </summary>
    public static string BuildScript()
    {
        var lines = new List<string>
        {
            "#!/bin/sh",
            "# Andy Containers introspection script",
            "# Output format: TYPE\\tNAME\\tVALUE (tab-separated)",
            "",
            "# Architecture",
            "printf 'ARCH\\t%s\\n' \"$(uname -m)\"",
            "",
            "# Kernel",
            "printf 'KERNEL\\t%s\\n' \"$(uname -r)\"",
            "",
            "# OS release",
            "if [ -f /etc/os-release ]; then",
            "  printf 'OS_RELEASE\\t'",
            "  cat /etc/os-release | tr '\\n' '|'",
            "  printf '\\n'",
            "fi",
            ""
        };

        // Tool detection
        foreach (var tool in ToolRegistry.KnownTools)
        {
            var whichCmd = tool.WhichCommand ?? tool.Name;

            // For tools that share a binary (dotnet-sdk and dotnet-runtime both use "dotnet"),
            // we still check each independently
            lines.Add($"# {tool.Name}");
            lines.Add($"if command -v {whichCmd} >/dev/null 2>&1; then");

            if (tool.UsesStdErr)
            {
                // Capture stderr (ssh -V, java --version write to stderr)
                lines.Add($"  _out=$({tool.DetectionCommand} 2>&1)");
            }
            else
            {
                lines.Add($"  _out=$({tool.DetectionCommand} 2>/dev/null)");
            }

            // Output: TOOL\t{name}\t{version output}\t{binary path}
            lines.Add($"  _path=$(command -v {whichCmd})");
            lines.Add($"  printf 'TOOL\\t{tool.Name}\\t%s\\t%s\\n' \"$_out\" \"$_path\"");
            lines.Add("fi");
            lines.Add("");
        }

        // Package detection — dpkg (Debian/Ubuntu) or apk (Alpine)
        lines.Add("# OS packages");
        lines.Add("if command -v dpkg-query >/dev/null 2>&1; then");
        lines.Add("  printf 'PACKAGES\\t'");
        lines.Add("  dpkg-query -W -f='${Package}\\t${Version}\\t${Architecture}\\n' 2>/dev/null | tr '\\n' '|'");
        lines.Add("  printf '\\n'");
        lines.Add("elif command -v apk >/dev/null 2>&1; then");
        lines.Add("  printf 'PACKAGES\\t'");
        lines.Add("  apk list --installed 2>/dev/null | tr '\\n' '|'");
        lines.Add("  printf '\\n'");
        lines.Add("fi");

        return string.Join("\n", lines) + "\n";
    }
}
