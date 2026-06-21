using Andy.Containers.Abstractions;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Abstractions;

// exec working-dir feature. Unit coverage for the shared command-wrap helper
// the non-Docker providers (Apple, SSH/cloud) use to honour the first-class
// WorkingDir field. Docker uses its native `-w` and never touches this; these
// tests prove the fallback's correctness and the mandatory backward-compat
// guarantee (null/empty ⇒ command unchanged).
public class ExecWorkingDirTests
{
    [Fact]
    public void Wrap_WithWorkingDir_PrefixesCdIntoThatDirectory()
    {
        var wrapped = ExecWorkingDir.Wrap("andy-cli run --headless", "/workspace");

        wrapped.Should().Be("cd '/workspace' && andy-cli run --headless");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Wrap_NullOrWhitespaceWorkingDir_ReturnsCommandUnchanged(string? workingDir)
    {
        // THE backward-compat guarantee: existing callers (including
        // andy-tasks' own cd-prefix workaround) must be byte-for-byte
        // unaffected when no working dir is supplied.
        const string command = "mkdir -p /tmp/x && andy-cli run";

        ExecWorkingDir.Wrap(command, workingDir).Should().Be(command);
    }

    [Fact]
    public void Wrap_SingleQuoteInDirectory_IsPosixEscaped()
    {
        // A single quote inside the path must close/escape/reopen so the
        // wrapped command stays a valid `/bin/sh -c` string.
        var wrapped = ExecWorkingDir.Wrap("ls", "/work'space");

        wrapped.Should().Be("cd '/work'\\''space' && ls");
    }

    [Fact]
    public void Wrap_TrimsSurroundingWhitespaceFromDirectory()
    {
        ExecWorkingDir.Wrap("ls", "  /workspace  ")
            .Should().Be("cd '/workspace' && ls");
    }
}
