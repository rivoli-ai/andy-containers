namespace Andy.Containers.Abstractions.Images;

/// <summary>
/// A built container image, identified by its OCI manifest digest.
/// The digest is the canonical key — the same bytes produce the same
/// digest in every registry. References (where the artifact is tagged)
/// are tracked separately via <see cref="RegistryReference"/>.
/// </summary>
/// <param name="Digest">
/// OCI manifest digest, e.g. <c>sha256:abc123...</c>. Globally unique.
/// </param>
/// <param name="MediaType">
/// OCI manifest media type, typically
/// <c>application/vnd.oci.image.manifest.v1+json</c>.
/// </param>
/// <param name="SizeBytes">Total uncompressed image size.</param>
/// <param name="SpecHash">
/// Content-addressable hash of the source <see cref="TemplateSpec"/> that
/// produced this artifact. Used as the idempotency key — the same spec
/// hashed to the same value should resolve to the same artifact, skipping
/// rebuild.
/// </param>
/// <param name="LocalReference">
/// Build-engine-local hint for where the bytes live before the registry
/// adapter pushes them. For <c>LocalBuildBackend</c> this is a Docker /
/// Apple Containers image ref the daemon just produced; for cloud build
/// backends it's a backend-specific identifier the matching registry
/// adapter understands.
/// </param>
public sealed record BuildArtifact(
    string Digest,
    string MediaType,
    long SizeBytes,
    string SpecHash,
    string LocalReference);
