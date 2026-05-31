using Andy.Containers.Api.Services;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// F4.1 (rivoli-ai/conductor#1934). The mid-run output stream echoes the
// agent's stdout/stderr to the operator. The run-scoped bearer
// (ANDY_TOKEN) must never reach that wire. RunOutputRedactor runs two
// passes; these tests pin both and the no-op cases.
public class RunOutputRedactorTests
{
    private const string Token = "sk-run-0123456789abcdef0123456789abcdef";

    [Fact]
    public void Redact_KnownToken_MasksEveryLiteralOccurrence()
    {
        // The agent could echo the bearer anywhere — a curl trace, a
        // debug dump — not just as ANDY_TOKEN=...; the known-secret pass
        // catches all of them.
        var line = $"curl -H 'Authorization: Bearer {Token}' https://api && echo {Token}";

        var result = RunOutputRedactor.Redact(line, Token);

        result.Should().NotContain(Token);
        result.Should().Contain(RunOutputRedactor.Placeholder);
        result.Should().Contain("Authorization: Bearer");
    }

    [Theory]
    [InlineData("ANDY_TOKEN=sk-secret-value-1234")]
    [InlineData("export ANDY_TOKEN=sk-secret-value-1234")]
    [InlineData("ANDY_TOKEN: sk-secret-value-1234")]
    [InlineData("\"ANDY_TOKEN\":\"sk-secret-value-1234\"")]
    public void Redact_EnvEcho_MasksValueEvenWithoutKnownToken(string line)
    {
        // We don't have the literal here (knownToken=null) — the env-echo
        // regex still masks the value so a `printenv`/`env` dump can't
        // leak the token.
        var result = RunOutputRedactor.Redact(line, knownToken: null);

        result.Should().NotContain("sk-secret-value-1234");
        result.Should().Contain(RunOutputRedactor.Placeholder);
        // The variable name / surrounding text survives so an operator
        // still sees "the token was here".
        result.Should().Contain("ANDY_TOKEN");
    }

    [Fact]
    public void Redact_EnvEcho_IsCaseInsensitiveOnKey()
    {
        var result = RunOutputRedactor.Redact("andy_token=leakme123", knownToken: null);
        result.Should().NotContain("leakme123");
    }

    [Fact]
    public void Redact_NonSecretLine_RoundTripsUnchanged()
    {
        const string line = "Iteration 3/4: planning the next edit";
        RunOutputRedactor.Redact(line, Token).Should().Be(line);
    }

    [Fact]
    public void Redact_ShortKnownToken_IsNotUsedForLiteralPass()
    {
        // A 1-2 char "token" would mangle unrelated text; the literal
        // pass only fires for tokens >= 8 chars. The env-echo pass is
        // unaffected.
        const string line = "the letter a appears in many words";
        RunOutputRedactor.Redact(line, knownToken: "a").Should().Be(line);
    }

    [Fact]
    public void Redact_NullOrEmpty_RoundTrips()
    {
        RunOutputRedactor.Redact(null, Token).Should().Be(string.Empty);
        RunOutputRedactor.Redact(string.Empty, Token).Should().Be(string.Empty);
    }
}
