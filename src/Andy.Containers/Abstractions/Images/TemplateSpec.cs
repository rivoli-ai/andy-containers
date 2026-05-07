namespace Andy.Containers.Abstractions.Images;

/// <summary>
/// Parsed and canonicalised template specification ready to be built.
/// Carries the structured fields the build backend consumes plus the
/// content-addressable hash + canonical-JSON form used for cache
/// lookups and audit.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SpecHash"/> is computed against
/// <see cref="CanonicalJson"/>, NOT the strongly-typed properties
/// below — those are a convenience view onto the same spec. Two
/// records that differ in the typed properties but share a
/// <see cref="CanonicalJson"/> + <see cref="SpecHash"/> are
/// equivalent under the cache.
/// </para>
/// <para>
/// IM7 (rivoli-ai/andy-containers#261) added the typed imperative
/// fields so <c>LocalBuildBackend</c> can render a Dockerfile
/// without re-parsing CanonicalJson. Going forward the orchestrator
/// is responsible for keeping <see cref="CanonicalJson"/> and the
/// typed properties in sync — easiest is to derive both from the
/// parsed YAML in one pass.
/// </para>
/// </remarks>
/// <param name="Code">Template code (e.g. <c>conductor-terminal-claude-code</c>).</param>
/// <param name="Version">Template version string.</param>
/// <param name="SpecHash">
/// SHA-256 hash of the canonical JSON serialisation of this spec plus the
/// digests of any uploaded files. Used as the build idempotency key.
/// </param>
/// <param name="CanonicalJson">
/// Canonical-JSON (RFC 8785 / JCS) serialisation of the parsed spec, used
/// for hashing and for storage. Stored as a string so the hash is
/// reproducible without re-parsing.
/// </param>
public sealed record TemplateSpec(
    string Code,
    string Version,
    string SpecHash,
    string CanonicalJson)
{
    /// <summary>
    /// OCI base image reference (e.g. <c>ubuntu:22.04</c>). Either
    /// this OR <see cref="Extends"/> must be set for a buildable spec.
    /// When both are set, <see cref="BaseImage"/> overrides the
    /// inherited base from the parent.
    /// </summary>
    public string? BaseImage { get; init; }

    /// <summary>
    /// Code of a parent template this spec extends. Resolved by the
    /// orchestrator at register-time via
    /// <c>TemplateExtendsCycleDetector</c>; build backends never see
    /// a chain — by the time <c>BuildAsync</c> is called, the chain
    /// has been collapsed.
    /// </summary>
    public string? Extends { get; init; }

    /// <summary>
    /// OS package names installed via the base image's package
    /// manager. The build backend chooses apt-get / yum / apk
    /// based on a heuristic over <see cref="BaseImage"/>.
    /// </summary>
    public IReadOnlyList<string> Packages { get; init; } = [];

    /// <summary>
    /// Files copied into the image at build time. The
    /// <see cref="TemplateFile.Source"/> field references the
    /// multipart-upload logical name.
    /// </summary>
    public IReadOnlyList<TemplateFile> Files { get; init; } = [];

    /// <summary>
    /// Shell command lines run after <see cref="Packages"/> and
    /// <see cref="Files"/> are processed. One layer per command for
    /// clean cache invalidation.
    /// </summary>
    public IReadOnlyList<string> Install { get; init; } = [];

    /// <summary>
    /// Container <c>ENTRYPOINT</c>. When null, the base image's
    /// entrypoint is inherited.
    /// </summary>
    public string? EntryPoint { get; init; }

    /// <summary>
    /// Free-form metadata about what's baked into the image. The
    /// build backend writes each key/value as a Docker <c>LABEL</c>
    /// so <c>docker inspect</c> round-trips the markers; the
    /// orchestrator additionally persists them on the
    /// <c>BuildArtifact.Markers</c> column.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Markers { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary>
/// One file uploaded with the spec, copied into the image during build.
/// </summary>
/// <param name="Source">
/// Multipart-upload logical name (the <c>files[<em>name</em>]</c>
/// part name in the request).
/// </param>
/// <param name="Dest">Absolute path inside the container.</param>
/// <param name="Mode">
/// Unix permission octal in <c>[0, 07777]</c>; null means inherit the
/// host file's mode (typically <c>0644</c>). Whatever the build
/// backend's COPY-then-chmod path produces.
/// </param>
public sealed record TemplateFile(
    string Source,
    string Dest,
    int? Mode = null);
