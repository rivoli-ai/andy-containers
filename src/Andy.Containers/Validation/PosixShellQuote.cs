namespace Andy.Containers.Validation;

/// <summary>
/// POSIX shell-quoting for values interpolated into commands run via
/// <c>/bin/sh -c</c> (or any other shell). Wraps the value in single
/// quotes and escapes any embedded single quote via <c>'\''</c> —
/// the canonical safe pattern: nothing inside single quotes is
/// interpreted by the shell, including <c>$</c>, <c>`</c>, and
/// backslash escapes.
/// </summary>
/// <remarks>
/// Used by the SSH provider helper (rivoli-ai/andy-containers#128)
/// to assemble remote <c>docker run</c> commands without the
/// remote shell tokenising on whitespace or expanding metacharacters.
/// Same pattern AP6's <c>HeadlessRunner.ShellEscape</c> uses for the
/// local <c>andy-cli</c> spawn — pulled into Andy.Containers/Validation
/// so both layers share a single implementation.
/// </remarks>
public static class PosixShellQuote
{
    /// <summary>
    /// Return <paramref name="value"/> wrapped in single quotes,
    /// safe for interpolation into a POSIX shell command. Null is
    /// rendered as the empty single-quoted string <c>''</c>.
    /// </summary>
    public static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "''";

        // Single quotes can't appear inside single quotes; close the
        // quoted segment, emit an escaped literal quote, reopen.
        // Result: 'foo'\''bar' for input foo'bar.
        return "'" + value.Replace("'", "'\\''") + "'";
    }
}
