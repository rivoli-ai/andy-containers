using Andy.Containers.Validation;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Validation;

// rivoli-ai/andy-containers#128. The SSH provider helper interpolates
// untrusted values (env values, image refs, command args) into shell
// commands sent to a remote sshd. PosixShellQuote is the safe-by-
// default helper used at every interpolation site; these tests pin
// the quoting contract.
public class PosixShellQuoteTests
{
    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("with spaces", "'with spaces'")]
    [InlineData("with$dollar", "'with$dollar'")]
    [InlineData("with`backticks`", "'with`backticks`'")]
    [InlineData("semi;colon&pipe|", "'semi;colon&pipe|'")]
    [InlineData("back\\slash", "'back\\slash'")]
    public void Quote_WrapsInSingleQuotes(string input, string expected)
    {
        // Inside single quotes the shell does not interpret $, `, \,
        // or any other metacharacter — pin that the helper exploits
        // that property correctly.
        PosixShellQuote.Quote(input).Should().Be(expected);
    }

    [Fact]
    public void Quote_EmbeddedSingleQuote_UsesEscapeSequence()
    {
        // Single quotes can't appear inside single-quoted strings, so
        // the canonical pattern is: close, emit \', reopen.
        // Input:   foo'bar
        // Output:  'foo'\''bar'
        PosixShellQuote.Quote("foo'bar").Should().Be("'foo'\\''bar'");
    }

    [Fact]
    public void Quote_MultipleSingleQuotes_AllEscaped()
    {
        PosixShellQuote.Quote("a'b'c").Should().Be("'a'\\''b'\\''c'");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Quote_NullOrEmpty_ReturnsEmptyQuotedString(string? input)
    {
        // Empty single-quoted string is the canonical "empty argv slot"
        // representation. Passing null / empty must round-trip cleanly
        // so callers don't pre-check.
        PosixShellQuote.Quote(input).Should().Be("''");
    }

    [Fact]
    public void Quote_OutputIsRoundTripSafe()
    {
        // Re-feeding the helper through itself wraps the wrapped form
        // — useful for layered commands (`sh -c '... sh -c '\''...'\'''`).
        // Validate that the inner-quote logic doesn't break on its own
        // output.
        var inner = PosixShellQuote.Quote("hello world");
        var outer = PosixShellQuote.Quote(inner);

        outer.Should().StartWith("'").And.EndWith("'");
        outer.Should().Contain("hello world",
            "the literal payload must round-trip through the double-wrap unchanged");
    }
}
