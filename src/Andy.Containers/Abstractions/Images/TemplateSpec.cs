namespace Andy.Containers.Abstractions.Images;

/// <summary>
/// Parsed and canonicalised template specification ready to be built.
/// </summary>
/// <remarks>
/// <para>
/// IM2 ships a minimal placeholder so the abstraction interfaces compile
/// against a stable type. IM4 (#253) extends this with the M1.9
/// imperative fields (<c>packages</c>, <c>files</c>, <c>install</c>,
/// <c>entrypoint</c>, <c>markers</c>, <c>extends</c>, <c>from</c>)
/// alongside the existing declarative <c>dependencies</c> model.
/// </para>
/// <para>
/// <see cref="SpecHash"/> is the content-addressable identity:
/// <c>sha256(canonicalJson(parsedSpec) || sortedFileDigests)</c>.
/// Two specs that differ only in YAML whitespace or key ordering produce
/// the same <see cref="SpecHash"/>.
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
    string CanonicalJson);
