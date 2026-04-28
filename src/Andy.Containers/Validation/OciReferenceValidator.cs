using System.Text.RegularExpressions;

namespace Andy.Containers.Validation;

/// <summary>
/// Validates OCI image references against a conservative subset of the
/// distribution-spec grammar. Used as defense-in-depth alongside the
/// argv-list path in image-build call sites
/// (rivoli-ai/andy-containers#126): even when the value lands in a
/// single argv slot, refusing visibly malformed references at the API
/// boundary surfaces operator typos with a clear error before the
/// docker daemon emits a less-actionable one.
/// </summary>
/// <remarks>
/// The full OCI grammar (digest algorithms, host:port + path, tags,
/// digest discriminators) is intricate. We accept what the
/// distribution-spec calls out as valid and reject anything containing
/// characters that have no business in an image reference: whitespace,
/// control characters, shell metacharacters. The intent is "obviously
/// wrong" detection, not protocol-perfect parsing — the daemon owns
/// the canonical decision.
/// </remarks>
public static partial class OciReferenceValidator
{
    // Rough regex that covers the common shape:
    //   [registry[:port]/]name[:tag][@digest]
    // - registry: hostname or hostname:port
    // - name: lowercase alphanumeric + a small set of separators (`._-`),
    //         optionally with `/`-separated path components
    // - tag: alphanumeric + `_.-`, max 128 chars (distribution-spec)
    // - digest: algo:hex, e.g. sha256:abcd...
    [GeneratedRegex(
        @"^(?:(?<registry>[a-zA-Z0-9][a-zA-Z0-9.\-]*(?::[0-9]+)?)/)?" +
        @"(?<name>[a-z0-9]+(?:[._\-/][a-z0-9]+)*)" +
        @"(?::(?<tag>[a-zA-Z0-9_][a-zA-Z0-9._\-]{0,127}))?" +
        @"(?:@(?<digest>[a-zA-Z][a-zA-Z0-9]*:[a-fA-F0-9]+))?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex OciReferencePattern();

    /// <summary>True when <paramref name="reference"/> matches the conservative grammar.</summary>
    public static bool IsValid(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return false;
        // Whitespace / control / quote / shell metachars never belong in
        // a reference. Reject up-front so a more pointed error message
        // beats a regex miss.
        foreach (var c in reference)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
            if (c is '"' or '\'' or '`' or '$' or '\\' or ';' or '|' or '&') return false;
        }
        return OciReferencePattern().IsMatch(reference);
    }

    /// <summary>
    /// Throw <see cref="ArgumentException"/> with a clear message when
    /// the reference is invalid. Use at API boundaries before launching
    /// build/inspect subprocesses.
    /// </summary>
    public static void Validate(string? reference, string paramName = "imageReference")
    {
        if (!IsValid(reference))
        {
            throw new ArgumentException(
                $"'{reference}' is not a valid OCI image reference. " +
                $"Expected [registry[:port]/]name[:tag][@digest] with no whitespace or shell metacharacters.",
                paramName);
        }
    }
}
