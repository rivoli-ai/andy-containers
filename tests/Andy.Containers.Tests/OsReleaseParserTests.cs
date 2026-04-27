using Andy.Containers;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests;

// Conductor #871. Locks the OS-label probe contract: input is
// the raw contents of /etc/os-release as written by every distro
// we plausibly run (Debian, Ubuntu, Alpine, Fedora, Arch, …).
// Output is a single short label the UI can show next to the
// friendly name. A null return means "we couldn't tell" — the UI
// renders nothing rather than guessing.
public class OsReleaseParserTests
{
    [Fact]
    public void ParseLabel_DebianStyle_ReturnsNameAndVersionId()
    {
        // Debian 12 ships os-release like this. Real sample.
        const string contents = """
            PRETTY_NAME="Debian GNU/Linux 12 (bookworm)"
            NAME="Debian GNU/Linux"
            VERSION_ID="12"
            VERSION="12 (bookworm)"
            VERSION_CODENAME=bookworm
            ID=debian
            HOME_URL="https://www.debian.org/"
            """;

        OsReleaseParser.ParseLabel(contents).Should().Be("Debian GNU/Linux 12");
    }

    [Fact]
    public void ParseLabel_AlpineStyle_StripsQuotesAndStitches()
    {
        const string contents = """
            NAME="Alpine Linux"
            ID=alpine
            VERSION_ID=3.19.1
            PRETTY_NAME="Alpine Linux v3.19"
            HOME_URL="https://alpinelinux.org/"
            """;

        OsReleaseParser.ParseLabel(contents).Should().Be("Alpine Linux 3.19.1");
    }

    [Fact]
    public void ParseLabel_UbuntuStyle_HandlesUnquotedVersionId()
    {
        const string contents = """
            NAME="Ubuntu"
            VERSION="24.04.1 LTS (Noble Numbat)"
            ID=ubuntu
            VERSION_ID="24.04"
            """;

        OsReleaseParser.ParseLabel(contents).Should().Be("Ubuntu 24.04");
    }

    [Fact]
    public void ParseLabel_RollingDistroWithoutVersionId_ReturnsNameAlone()
    {
        // Arch deliberately omits VERSION_ID because it rolls.
        const string contents = """
            NAME="Arch Linux"
            ID=arch
            BUILD_ID=rolling
            PRETTY_NAME="Arch Linux"
            """;

        OsReleaseParser.ParseLabel(contents).Should().Be("Arch Linux");
    }

    [Fact]
    public void ParseLabel_NullInput_ReturnsNull()
    {
        OsReleaseParser.ParseLabel(null).Should().BeNull();
    }

    [Fact]
    public void ParseLabel_EmptyInput_ReturnsNull()
    {
        OsReleaseParser.ParseLabel("").Should().BeNull();
        OsReleaseParser.ParseLabel("   \n  ").Should().BeNull();
    }

    [Fact]
    public void ParseLabel_NoNameKey_ReturnsNull()
    {
        // Without NAME, we can't say anything useful — return
        // null so the UI renders no label rather than "12".
        const string contents = "VERSION_ID=12\nID=debian";

        OsReleaseParser.ParseLabel(contents).Should().BeNull();
    }

    [Fact]
    public void ParseLabel_IgnoresCommentsAndBlankLines()
    {
        const string contents = """

            # this is a comment
            NAME="Debian"
            # another comment
            VERSION_ID=12

            """;

        OsReleaseParser.ParseLabel(contents).Should().Be("Debian 12");
    }

    [Fact]
    public void ParseLabel_HandlesSingleQuotedValues()
    {
        // Per the spec, both quote styles are valid.
        const string contents = "NAME='Custom Distro'\nVERSION_ID='1.0'";

        OsReleaseParser.ParseLabel(contents).Should().Be("Custom Distro 1.0");
    }

    [Fact]
    public void ParseLabel_GarbageInput_ReturnsNull()
    {
        // The probe path appends `|| true` so we can get back
        // arbitrary garbage from misbehaving images. Should not
        // throw; should return null.
        OsReleaseParser.ParseLabel("not\xa key=value file at all").Should().BeNull();
    }
}
