using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Andy.Containers.Crypto;

/// <summary>
/// Practical canonical-JSON serialiser used by image management for
/// content-addressable spec hashing. Produces a stable byte sequence
/// for any equivalent JSON input regardless of incoming whitespace,
/// key ordering, or member-emit order — so two YAML specs that differ
/// only in those respects hash to the same value.
/// </summary>
/// <remarks>
/// <para>
/// Implements the subset of RFC 8785 (JSON Canonicalization Scheme)
/// that matters for hashing template specs: recursive lexicographic
/// key sorting at every object level, no whitespace, and
/// <see cref="System.Text.Json"/>'s default minimal number / string
/// escaping. This is sufficient for the YAML-derived specs IM3 hashes
/// (integers, booleans, strings, arrays of those, and nested
/// objects).
/// </para>
/// <para>
/// Full RFC 8785 conformance — in particular, the number normalisation
/// rules for IEEE-754 doubles (no leading zeros, no insignificant
/// trailing zeros, exponent normalisation for very small / large
/// magnitudes) — is intentionally not implemented here. If a future
/// spec field needs precision-sensitive floats, swap this for a
/// dedicated JCS library at that point.
/// </para>
/// </remarks>
public static class CanonicalJson
{
    /// <summary>
    /// Parse an arbitrary JSON document and emit its canonical form
    /// as UTF-8 bytes. The output is stable for equivalent inputs
    /// regardless of whitespace or property order in the source.
    /// </summary>
    /// <param name="json">JSON document to canonicalise.</param>
    /// <returns>Canonical UTF-8 bytes.</returns>
    /// <exception cref="JsonException">If <paramref name="json"/> is not valid JSON.</exception>
    public static byte[] Serialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json);
        return SerializeElement(doc.RootElement);
    }

    /// <summary>
    /// Emit the canonical form of an already-parsed JSON element.
    /// </summary>
    public static byte[] SerializeElement(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            // No whitespace; preserve ASCII range as-is so
            // canonicalisation doesn't reshape strings beyond what
            // JSON requires.
            Indented = false,
            SkipValidation = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            WriteCanonical(writer, element);
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Compute the SHA-256 hash of the canonical form of a JSON
    /// document, returned in the standard <c>sha256:...</c>
    /// representation used elsewhere in image management.
    /// </summary>
    public static string Hash(string json)
    {
        var bytes = Serialize(json);
        return HashBytes(bytes);
    }

    /// <summary>
    /// Compute the SHA-256 hash of pre-canonicalised UTF-8 bytes.
    /// </summary>
    public static string HashBytes(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Compute a content-addressable spec hash by combining the
    /// canonical JSON of the spec with the digests of any referenced
    /// upload files in lexicographic logical-name order. This matches
    /// the formula documented in the IM1 architecture memo:
    /// <c>sha256(canonicalJson(parsedSpec) || sortedFileDigests)</c>.
    /// </summary>
    /// <param name="canonicalSpecBytes">
    /// Canonical-JSON form of the parsed spec (call <see cref="Serialize"/>
    /// first).
    /// </param>
    /// <param name="fileDigests">
    /// File digests keyed by their logical name (the multipart
    /// <c>files[name]</c> token). Order does not matter — entries are
    /// sorted before being hashed.
    /// </param>
    public static string ComputeSpecHash(
        ReadOnlySpan<byte> canonicalSpecBytes,
        IReadOnlyDictionary<string, string> fileDigests)
    {
        ArgumentNullException.ThrowIfNull(fileDigests);

        // Hash spec || sortedFileDigests. The "||" separator between
        // each contributing chunk is a single 0x00 byte so two
        // adjacent string fields can't be confused with a single
        // longer string.
        using var sha = SHA256.Create();
        sha.TransformBlock(
            canonicalSpecBytes.ToArray(),
            inputOffset: 0,
            inputCount: canonicalSpecBytes.Length,
            outputBuffer: null,
            outputOffset: 0);

        ReadOnlySpan<byte> separator = stackalloc byte[] { 0x00 };

        foreach (var (logicalName, digest) in fileDigests.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            var nameBytes = Encoding.UTF8.GetBytes(logicalName);
            var digestBytes = Encoding.UTF8.GetBytes(digest);

            sha.TransformBlock(separator.ToArray(), 0, separator.Length, null, 0);
            sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
            sha.TransformBlock(separator.ToArray(), 0, separator.Length, null, 0);
            sha.TransformBlock(digestBytes, 0, digestBytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return "sha256:" + Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                // Lexicographic UTF-16 code unit order per RFC 8785.
                // StringComparer.Ordinal compares UTF-16 code units in
                // C# strings, which matches the spec.
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                // Round-trip via the raw text form — this preserves
                // the source representation (no implicit conversion
                // to double) which is what callers want for stable
                // hashing of bigint / decimal values.
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported JSON value kind '{element.ValueKind}' encountered during canonicalisation.");
        }
    }
}
