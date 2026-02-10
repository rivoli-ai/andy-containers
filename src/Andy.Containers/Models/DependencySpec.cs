namespace Andy.Containers.Models;

/// <summary>
/// Declares a tool, compiler, or library dependency for a container template.
/// The user specifies what they want; the build system resolves exact versions.
/// </summary>
public class DependencySpec
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public ContainerTemplate? Template { get; set; }

    /// <summary>
    /// Dependency type: compiler, runtime, tool, library, os-package, etc.
    /// </summary>
    public DependencyType Type { get; set; }

    /// <summary>
    /// Dependency name (e.g., "dotnet-sdk", "python", "node", "angular-cli", "numpy").
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Ecosystem for library dependencies (e.g., "nuget", "pip", "npm").
    /// Null for compilers and system tools.
    /// </summary>
    public string? Ecosystem { get; set; }

    /// <summary>
    /// Version constraint (semver range or "latest").
    /// Examples: "8.0.*", ">=3.12,<4.0", "18.x", "latest", "3.12.1" (pinned).
    /// </summary>
    public required string VersionConstraint { get; set; }

    /// <summary>
    /// Whether to automatically rebuild when a new version matching the constraint is available.
    /// </summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>
    /// Priority for updates: security-only, minor, major, all.
    /// </summary>
    public UpdatePolicy UpdatePolicy { get; set; } = UpdatePolicy.Minor;

    public int SortOrder { get; set; }
    public string? Metadata { get; set; }
}

/// <summary>
/// A resolved (locked) dependency version from a specific build.
/// </summary>
public class ResolvedDependency
{
    public Guid Id { get; set; }
    public Guid ImageId { get; set; }
    public ContainerImage? Image { get; set; }
    public Guid DependencySpecId { get; set; }
    public DependencySpec? DependencySpec { get; set; }

    /// <summary>
    /// Exact resolved version (e.g., "8.0.404", "3.12.8", "20.18.1").
    /// </summary>
    public required string ResolvedVersion { get; set; }

    /// <summary>
    /// Source URL or feed from which the dependency was obtained.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// SHA-256 hash of the downloaded artifact for integrity verification.
    /// </summary>
    public string? ArtifactHash { get; set; }

    /// <summary>
    /// Size of the artifact in bytes.
    /// </summary>
    public long? ArtifactSizeBytes { get; set; }

    /// <summary>
    /// Whether this version was available from the offline/air-gapped cache.
    /// </summary>
    public bool FromOfflineCache { get; set; }

    public DateTime ResolvedAt { get; set; } = DateTime.UtcNow;
}

public enum DependencyType
{
    Compiler,
    Runtime,
    Sdk,
    Tool,
    OsPackage,
    Library,
    Extension,
    Image
}

public enum UpdatePolicy
{
    /// <summary>Only rebuild for security patches.</summary>
    SecurityOnly,

    /// <summary>Rebuild for patch version updates (e.g., 8.0.3 → 8.0.4).</summary>
    Patch,

    /// <summary>Rebuild for minor version updates (e.g., 8.0 → 8.1).</summary>
    Minor,

    /// <summary>Rebuild for any version update including major.</summary>
    Major,

    /// <summary>Never auto-rebuild; only manual triggers.</summary>
    Manual
}
