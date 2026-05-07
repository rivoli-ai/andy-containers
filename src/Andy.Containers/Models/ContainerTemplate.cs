namespace Andy.Containers.Models;

public class ContainerTemplate
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string Version { get; set; }
    public required string BaseImage { get; set; }
    public CatalogScope CatalogScope { get; set; } = CatalogScope.Global;
    public Guid? OrganizationId { get; set; }
    public Guid? TeamId { get; set; }
    public string? OwnerId { get; set; }
    public string? Toolchains { get; set; }
    public IdeType IdeType { get; set; } = IdeType.CodeServer;
    public string GuiType { get; set; } = "none"; // "none" or "vnc"
    public string? DefaultResources { get; set; }
    public bool GpuRequired { get; set; }
    public bool GpuPreferred { get; set; }
    public string? EnvironmentVariables { get; set; }
    public string? Ports { get; set; }
    public string? Scripts { get; set; }
    public string[]? Tags { get; set; }
    public bool IsPublished { get; set; }
    public Guid? ParentTemplateId { get; set; }
    public ContainerTemplate? ParentTemplate { get; set; }
    public string? GitRepositories { get; set; }
    public string? CodeAssistant { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public string? Metadata { get; set; }

    /// <summary>
    /// Optional reference to a <see cref="Theme"/> id. Containers
    /// created from this template inherit this as their initial
    /// theme. Operators can change it later via the template
    /// detail editor; existing containers are NOT retroactively
    /// updated (per-container theme overrides are sticky).
    /// Conductor #886.
    /// </summary>
    public string? ThemeId { get; set; }

    // IM4 (rivoli-ai/andy-containers#253). M1.9 imperative-style
    // fields, additive to the existing declarative dependencies model.
    // Persisted on the template so the build backend can replay them
    // deterministically; the spec hash is computed over the canonical
    // form of *all* fields, so changing any of these triggers a
    // content-addressable cache miss and a rebuild.

    /// <summary>
    /// Optional code of another template this one extends. The build
    /// pipeline resolves this at register-time, walking the chain in
    /// the templates table. Cycles are rejected at register-time —
    /// see <c>TemplateExtendsCycleDetector</c>.
    /// </summary>
    public string? Extends { get; set; }

    /// <summary>
    /// JSON array of OS package names installed via the base image's
    /// package manager (apt-get / yum / apk, picked by the build
    /// backend based on the base image). Null when the spec doesn't
    /// declare any.
    /// </summary>
    public string? Packages { get; set; }

    /// <summary>
    /// JSON array of <c>{ source, dest, mode }</c> entries describing
    /// files to copy into the image during the build. <c>source</c>
    /// is the multipart-upload logical name; <c>dest</c> is an
    /// absolute path inside the container; <c>mode</c> is octal.
    /// </summary>
    public string? Files { get; set; }

    /// <summary>
    /// JSON array of shell commands run in order after
    /// <see cref="Packages"/> are installed and <see cref="Files"/>
    /// are copied. Each entry is a single shell line passed to the
    /// build backend's image-builder step.
    /// </summary>
    public string? Install { get; set; }

    /// <summary>
    /// Optional container <c>ENTRYPOINT</c>. When set, overrides any
    /// entrypoint inherited from <see cref="BaseImage"/> or from the
    /// <see cref="Extends"/> parent.
    /// </summary>
    public string? EntryPoint { get; set; }

    /// <summary>
    /// JSON object of free-form key/value metadata about what's baked
    /// into the resulting image (e.g.
    /// <c>{ "baked-assistants": ["claude-code"] }</c>). Surfaced via
    /// <c>GET /api/images</c> so launch UIs can label images
    /// without having to introspect the filesystem.
    /// </summary>
    public string? Markers { get; set; }

    /// <summary>
    /// IM8 (rivoli-ai/andy-containers#262). Content-addressable hash
    /// of the spec at register-time —
    /// <c>sha256(canonicalJson(parsedSpec) || sortedFileDigests)</c>.
    /// Indexed alongside the template id so the orchestrator can
    /// short-circuit a build when an artifact already exists for
    /// this template + this spec. Null on legacy rows that pre-date
    /// IM8; populated on every register-from-yaml call thereafter.
    /// </summary>
    public string? SpecHash { get; set; }
}

public enum CatalogScope
{
    Global,
    Organization,
    Team,
    User
}

public enum IdeType
{
    None,
    CodeServer,
    Zed,
    Both
}
