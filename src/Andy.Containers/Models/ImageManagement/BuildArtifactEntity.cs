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
    /// Captured stdout/stderr from the build engine on failure.
    /// Populated when <c>BuildArtifactEntity</c> is created for a
    /// failed build (so the API can surface the log via 422). Null on
    /// successful builds.
    /// </summary>
    public string? BuildLog { get; set; }

    /// <summary>References pointing at this artifact across registries.</summary>
    public ICollection<RegistryReferenceEntity> References { get; set; } = [];

    /// <summary>Signatures attesting to the integrity of this artifact.</summary>
    public ICollection<ImageSignature> Signatures { get; set; } = [];
}
