namespace Andy.Containers.Models;

/// <summary>
/// Operating system details captured during image introspection.
/// </summary>
public class OsInfo
{
    /// <summary>OS distribution name (e.g., "Ubuntu", "Alpine").</summary>
    public required string Name { get; set; }

    /// <summary>OS version (e.g., "24.04", "3.19").</summary>
    public required string Version { get; set; }

    /// <summary>OS codename (e.g., "noble", "bookworm").</summary>
    public required string Codename { get; set; }

    /// <summary>Kernel version from uname -r.</summary>
    public required string KernelVersion { get; set; }
}
