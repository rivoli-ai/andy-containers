namespace Andy.Containers.Models;

/// <summary>
/// An OS-level package detected during image introspection.
/// </summary>
public class InstalledPackage
{
    /// <summary>Package name (e.g., "libssl3", "openssh-client").</summary>
    public required string Name { get; set; }

    /// <summary>Package version string.</summary>
    public required string Version { get; set; }

    /// <summary>Package architecture (e.g., "amd64", "arm64").</summary>
    public string? Architecture { get; set; }

    /// <summary>Source repository (e.g., apt repo URL).</summary>
    public string? Source { get; set; }
}
