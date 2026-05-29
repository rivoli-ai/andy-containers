using Andy.Containers.Models;

namespace Andy.Containers.Models.ImageManagement;

/// <summary>
/// Persisted form of a built container image identified by its OCI
/// manifest digest. The digest is the canonical key — same bytes, same
/// digest, in every registry. This row is the audit / signing /
/// deduplication anchor; <see cref="RegistryReferenceEntity"/> rows
/// point at it from each registry the image has been pushed to.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <c>Andy.Containers.Abstractions.Images.BuildArtifact</c>
/// (the transport record returned by build backends). The repository
/// (<c>IBuildArtifactStore</c>) maps between the two at the storage
/// boundary so consumers of the abstraction never see EF-specific types.
/// </para>
/// <para>
/// Layered on top of the existing <see cref="ContainerImage"/> table
/// rather than replacing it: each pre-existing <c>ContainerImage</c>
/// row keeps its template-build-centric metadata, and new builds
/// post-IM3 populate both <see cref="BuildArtifactEntity"/> and
/// <see cref="ContainerImage"/> in the same transaction.
/// <see cref="ContainerImage.BuildArtifactId"/> links the two.
/// </para>
/// </remarks>
public class BuildArtifactEntity
{
    public Guid Id { get; set; }

    /// <summary>
    /// OCI manifest digest, e.g. <c>sha256:abc123...</c>. Globally
    /// unique across registries — same bytes produce the same digest
    /// in every registry.
    /// </summary>
    public required string Digest { get; set; }

    /// <summary>
    /// OCI manifest media type, typically
    /// <c>application/vnd.oci.image.manifest.v1+json</c>.
    /// </summary>
    public required string MediaType { get; set; }

    /// <summary>Total uncompressed image size.</summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Content-addressable hash of the source spec that produced this
    /// artifact —
    /// <c>sha256(canonicalJson(parsedSpec) || sortedFileDigests)</c>.
    /// Two specs that differ only in YAML whitespace or key ordering
    /// produce the same <see cref="SpecHash"/>; that's the idempotency
    /// guarantee. Indexed for content-addressable cache lookups.
    /// </summary>
    public required string SpecHash { get; set; }

    /// <summary>
    /// Owning template. Same FK relationship as
    /// <see cref="ContainerImage.TemplateId"/>.
    /// </summary>
    public Guid TemplateId { get; set; }
    public ContainerTemplate? Template { get; set; }

    /// <summary>
    /// Identifier of the build backend that produced this artifact —
    /// matches <c>IBuildBackend.BackendId</c> (e.g.
    /// <c>local-docker</c>, <c>apple-containers</c>, <c>acr-tasks</c>).
    /// </summary>
    public required string BuildBackendId { get; set; }

    /// <summary>
    /// Principal that triggered the build (user id or service identity).
    /// </summary>
    public required string BuiltBy { get; set; }

    /// <summary>When the artifact's manifest was finalised.</summary>
    public DateTime BuiltAt { get; set; }

    /// <summary>
    /// Captured stdout/stderr from the build engine. Populated by
    /// <c>ImageBuildOrchestrator</c> from the build progress stream
    /// (<c>BuildStepStdoutEvent</c> / <c>BuildStepErrorEvent</c>),
    /// truncated to a bounded size. Null when the build produced no
    /// log output (or for legacy rows persisted before #320's build-log
    /// capture landed). The full, untruncated log is also uploaded to
    /// andy-docs — see <see cref="BuildLogDocsRef"/>.
    /// </summary>
    public string? BuildLog { get; set; }

    /// <summary>
    /// rivoli-ai/andy-containers#320 (build-log companion to the
    /// OutputArtifact byte-upload). Pointer into andy-docs for the
    /// uploaded <see cref="BuildLog"/>, stamped by
    /// <c>ImageBuildOrchestrator</c> after a successful
    /// <c>POST /api/documents:put</c>. <c>null</c> when:
    /// <list type="bullet">
    ///   <item>The orchestrator ran with no <c>IAndyDocsClient</c>
    ///   registered (no <c>AndyDocs:ApiBaseUrl</c> — dev / embedded
    ///   mode; <see cref="BuildLog"/> is still persisted inline).</item>
    ///   <item>The andy-docs upload failed (transient network error,
    ///   5xx, timeout). The build still succeeds and
    ///   <see cref="BuildLog"/> is persisted inline; consumers treat a
    ///   null ref as "log not pinned in andy-docs".</item>
    ///   <item>The build produced no log to upload.</item>
    /// </list>
    /// Mapped as an EF owned type onto two nullable columns
    /// (<c>BuildLogDocsRefDocumentId</c> / <c>BuildLogDocsRefLinkId</c>);
    /// best-effort, so andy-docs availability never blocks a build.
    /// </summary>
    public DocsRef? BuildLogDocsRef { get; set; }

    /// <summary>References pointing at this artifact across registries.</summary>
    public ICollection<RegistryReferenceEntity> References { get; set; } = [];

    /// <summary>Signatures attesting to the integrity of this artifact.</summary>
    public ICollection<ImageSignature> Signatures { get; set; } = [];
}
