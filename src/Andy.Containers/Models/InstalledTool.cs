namespace Andy.Containers.Models;

/// <summary>
/// A tool, SDK, or runtime detected during image introspection.
/// </summary>
public class InstalledTool
{
    /// <summary>Tool name matching DependencySpec.Name (e.g., "dotnet-sdk", "python").</summary>
    public required string Name { get; set; }

    /// <summary>Exact detected version (e.g., "8.0.404", "3.12.8").</summary>
    public required string Version { get; set; }

    /// <summary>Tool category.</summary>
    public DependencyType Type { get; set; }

    /// <summary>Version constraint declared in the template (null if undeclared).</summary>
    public string? DeclaredVersion { get; set; }

    /// <summary>Whether the actual version satisfies the declared constraint.</summary>
    public bool MatchesDeclared { get; set; } = true;

    /// <summary>Installation directory (e.g., "/usr/share/dotnet").</summary>
    public string? InstallPath { get; set; }

    /// <summary>Path to the executable binary.</summary>
    public string? BinaryPath { get; set; }

    /// <summary>Approximate disk usage in bytes.</summary>
    public long? SizeBytes { get; set; }
}
