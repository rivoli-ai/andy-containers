namespace Andy.Containers.Models;

/// <summary>
/// Severity of a version change between two images.
/// </summary>
public enum ChangeSeverity
{
    /// <summary>Versions are identical (build metadata only).</summary>
    Build,

    /// <summary>Only patch version differs (e.g., 8.0.400 → 8.0.404).</summary>
    Patch,

    /// <summary>Minor version differs (e.g., 8.0.x → 8.1.x).</summary>
    Minor,

    /// <summary>Major version differs (e.g., 8.x → 9.x).</summary>
    Major
}
