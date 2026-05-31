using System.Text.RegularExpressions;
using Andy.Containers.Configurator;

namespace Andy.Containers.Api.Services;

/// <summary>
/// F4.1 (rivoli-ai/conductor#1934). Redacts the run-scoped credential
/// (<c>ANDY_TOKEN</c>, see <c>docs/runs.md</c> "Run-scoped credentials")
/// from a mid-run output line before it is echoed onto the live output
/// stream.
/// </summary>
/// <remarks>
/// Two passes, both cheap and order-independent:
///   1. <b>Known-secret pass</b> — when the runner knows the exact
///      run-scoped token string (from the issuer, which mints
///      idempotently), every literal occurrence is replaced. This is the
///      authoritative pass: the agent could echo the bearer anywhere
///      (a curl trace, a debug dump), not just as <c>ANDY_TOKEN=...</c>.
///   2. <b>Env-echo pass</b> — defensive regex over
///      <c>ANDY_TOKEN=&lt;value&gt;</c> (and the <c>export</c>,
///      <c>"ANDY_TOKEN": "..."</c>, and bare-assignment shapes) so a
///      token we DON'T have the literal for (an issuer that returns null,
///      a future token rotated mid-run) still doesn't leak when the
///      agent dumps its environment.
///
/// Redaction preserves the variable name / surrounding text and replaces
/// only the secret value with <see cref="Placeholder"/> so an operator
/// reading the stream still sees "the token was here" without seeing the
/// token.
/// </remarks>
public static class RunOutputRedactor
{
    public const string Placeholder = "***";

    // Matches ANDY_TOKEN being assigned a value in the common shell /
    // dotenv / JSON shapes. The value group is everything up to the next
    // whitespace, quote, comma, or end-of-line — enough to catch
    // `ANDY_TOKEN=abc`, `export ANDY_TOKEN=abc`, `ANDY_TOKEN: abc`,
    // `"ANDY_TOKEN":"abc"`. Case-insensitive on the key only.
    private static readonly Regex EnvEchoPattern = new(
        $"(?<prefix>{Regex.Escape(EnvVarNames.AndyToken)}\"?\\s*[=:]\\s*\"?)(?<value>[^\\s\",}}]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Redact <paramref name="line"/>. <paramref name="knownToken"/> is
    /// the literal run-scoped bearer when the runner has it (preferred);
    /// pass null/empty to rely on the env-echo pass alone. Null / empty
    /// lines round-trip unchanged.
    /// </summary>
    public static string Redact(string? line, string? knownToken)
    {
        if (string.IsNullOrEmpty(line))
        {
            return line ?? string.Empty;
        }

        var result = line;

        // Pass 1: literal secret. Only worth running when we actually
        // have a non-trivial token (a 1-2 char token would mangle
        // unrelated text; run-scoped tokens are always long).
        if (!string.IsNullOrEmpty(knownToken) && knownToken.Length >= 8)
        {
            result = result.Replace(knownToken, Placeholder, StringComparison.Ordinal);
        }

        // Pass 2: ANDY_TOKEN=<value> env-echo, regardless of whether we
        // had the literal.
        result = EnvEchoPattern.Replace(result, m => m.Groups["prefix"].Value + Placeholder);

        return result;
    }
}
