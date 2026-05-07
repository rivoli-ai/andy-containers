namespace Andy.Containers.Models.ImageManagement;

/// <summary>
/// A signature attesting to the integrity of a
/// <see cref="BuildArtifactEntity"/>. Stored alongside the artifact
/// rather than as a separate registry reference so the audit trail is
/// authoritative even if the registry-side referrer record is lost.
/// </summary>
public class ImageSignature
{
    public Guid Id { get; set; }

    /// <summary>Owning artifact.</summary>
    public Guid BuildArtifactId { get; set; }
    public BuildArtifactEntity? BuildArtifact { get; set; }

    /// <summary>
    /// Signature scheme. <see cref="ImageSignatureFormat.CosignKeyless"/>
    /// is the default in IM12; <see cref="ImageSignatureFormat.NotationV2"/>
    /// is added when a customer requires it.
    /// </summary>
    public ImageSignatureFormat Format { get; set; }

    /// <summary>
    /// Digest of the signed payload (the manifest digest plus any
    /// claims included by the signing tool).
    /// </summary>
    public required string PayloadDigest { get; set; }

    /// <summary>
    /// PEM-encoded certificate chain — populated for
    /// <see cref="ImageSignatureFormat.CosignKeyless"/> (Fulcio cert)
    /// and unused for keypair signing.
    /// </summary>
    public string? CertificateChain { get; set; }

    /// <summary>
    /// Rekor / transparency-log entry UUID for keyless signing. Used
    /// when verifying the signature long after issuance.
    /// </summary>
    public string? TransparencyLogEntry { get; set; }

    /// <summary>When the signature was issued.</summary>
    public DateTime SignedAt { get; set; }
}

/// <summary>
/// Supported signature formats. Notary v1 is intentionally absent —
/// Harbor 2.9+ removed it and we won't carry the legacy weight.
/// </summary>
public enum ImageSignatureFormat
{
    CosignKeyless,
    CosignKeypair,
    NotationV2,
}
