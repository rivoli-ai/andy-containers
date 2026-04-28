using Andy.Containers.Api.Services;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// rivoli-ai/andy-containers#131. The redactor sits in the hot path for
// every code-assistant injection log line; it must redact reliably,
// preserve enough of the URL for log readers to identify the upstream,
// and never crash on malformed input. These tests pin those three
// contracts.
public class UrlRedactorTests
{
    [Fact]
    public void Redact_UserAndPassword_MasksPasswordOnly()
    {
        var redacted = UrlRedactor.Redact("https://alice:s3cret@proxy.example.com/v1");

        redacted.Should().Contain("alice", "the user identifier is not the credential here");
        redacted.Should().NotContain("s3cret");
        redacted.Should().Contain("***");
        redacted.Should().Contain("proxy.example.com/v1",
            "log readers still need to identify the upstream service");
    }

    [Fact]
    public void Redact_BareToken_MasksUser()
    {
        // Many OpenAI-compatible proxies pass the token alone in the
        // user position (no colon, no password). The "user" string is
        // the credential in that case.
        var redacted = UrlRedactor.Redact("https://sk-proj-abc123@proxy.example.com/v1");

        redacted.Should().NotContain("sk-proj-abc123");
        redacted.Should().Contain("***@proxy.example.com");
    }

    [Fact]
    public void Redact_NoUserInfo_ReturnsUnchanged()
    {
        const string clean = "https://api.openai.com/v1";

        UrlRedactor.Redact(clean).Should().Be(clean);
    }

    [Fact]
    public void Redact_PortAndPathPreserved()
    {
        var redacted = UrlRedactor.Redact("https://u:p@proxy.example.com:8443/v1/chat/completions?stream=true");

        redacted.Should().Contain("proxy.example.com:8443");
        redacted.Should().Contain("/v1/chat/completions");
        redacted.Should().Contain("stream=true");
        redacted.Should().NotContain(":p@");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Redact_NullOrEmptyOrBlank_RoundTrips(string? input)
    {
        UrlRedactor.Redact(input).Should().Be(input ?? string.Empty,
            "callers expect to feed any code-assistant config straight through without pre-checks");
    }

    [Fact]
    public void Redact_MalformedInput_ReturnsPlaceholder()
    {
        // A pasted credential that's not a URL at all — e.g. the
        // operator misconfigured ApiBaseUrl. Better to log a
        // placeholder than the raw bytes that may themselves be a
        // secret.
        var redacted = UrlRedactor.Redact("sk-proj-abc123");

        redacted.Should().Be("<invalid-url>");
        redacted.Should().NotContain("sk-proj-abc123");
    }

    [Fact]
    public void Redact_Ipv6HostPreserved()
    {
        // IPv6 brackets are a common parser-trip in hand-rolled
        // redactors. UriBuilder handles them correctly; pin that
        // explicitly.
        var redacted = UrlRedactor.Redact("https://u:p@[::1]:8080/v1");

        redacted.Should().Contain("[::1]:8080");
        redacted.Should().NotContain(":p@");
    }
}
