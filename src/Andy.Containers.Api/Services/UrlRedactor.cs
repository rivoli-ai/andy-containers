namespace Andy.Containers.Api.Services;

/// <summary>
/// Sanitises URLs before they hit log sinks. The platform's
/// observability stack ships logs to OTLP and shared infrastructure;
/// any structured argument that ends up in the message body is at risk
/// of cross-tenant leakage if it embeds credentials.
/// </summary>
/// <remarks>
/// Code-assistant config routinely supplies OpenAI-compatible proxy
/// URLs of the form <c>https://user:token@proxy.example.com/v1</c>;
/// the userinfo portion is the credential. Redaction preserves the
/// scheme, host, port, path, and query (so log readers can still
/// identify the upstream service) and replaces only the userinfo with
/// <c>user:***</c> or <c>***</c>. Malformed inputs round-trip a fixed
/// <c>&lt;invalid-url&gt;</c> token rather than the raw string so a
/// pasted credential without a scheme still doesn't reach the sink.
/// </remarks>
public static class UrlRedactor
{
    private const string MalformedToken = "<invalid-url>";
    private const string RedactionPlaceholder = "***";

    /// <summary>
    /// Return <paramref name="url"/> with any userinfo component
    /// redacted. Null / empty / whitespace inputs round-trip
    /// unchanged so callers don't have to pre-check.
    /// </summary>
    public static string Redact(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url ?? string.Empty;

        // Use UriBuilder rather than parsing userinfo by hand: it
        // handles ports, IPv6 hosts, and percent-encoded bytes that a
        // regex would mishandle. Prefer absolute parse so a relative
        // path doesn't masquerade as a URL with credentials.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return MalformedToken;
        }

        if (string.IsNullOrEmpty(parsed.UserInfo))
        {
            // Nothing to redact — return the original string rather
            // than the round-tripped one so the log line preserves the
            // caller's exact spelling (trailing slashes, casing, etc).
            return url;
        }

        var builder = new UriBuilder(parsed);

        // Format depends on whether a password component was present:
        // user:token@ → user:***@   (preserve the user identifier)
        // bareuser@   → ***@        (the "user" alone may be a token)
        if (string.IsNullOrEmpty(builder.Password))
        {
            builder.UserName = RedactionPlaceholder;
        }
        else
        {
            builder.Password = RedactionPlaceholder;
        }

        // UriBuilder.Uri.ToString() round-trips with default port
        // elision and percent-encoding normalisation. Acceptable for a
        // log line — the redacted URL is for human / scraper
        // identification, not for re-fetching.
        return builder.Uri.ToString();
    }
}
