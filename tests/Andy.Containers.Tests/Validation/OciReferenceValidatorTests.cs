using Andy.Containers.Validation;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Validation;

// rivoli-ai/andy-containers#126. The validator is defense-in-depth on
// the buildx + image-inspect call sites. Argv-list construction is the
// primary fix — these tests pin the secondary check so a future caller
// landing the validator on a new entry point gets the same behaviour.
public class OciReferenceValidatorTests
{
    [Theory]
    // Plain images
    [InlineData("ubuntu", true)]
    [InlineData("ubuntu:24.04", true)]
    [InlineData("ubuntu:24.04-lts", true)]
    // Registry-prefixed
    [InlineData("ghcr.io/rivoli-ai/andy-headless:latest", true)]
    [InlineData("registry.rivoli.ai:5000/team/repo:v1.2.3", true)]
    // Digest-pinned
    [InlineData("ubuntu@sha256:1a2b3c4d5e6f7890aabbccddeeff00112233445566778899aabbccddeeff0011", true)]
    [InlineData("ghcr.io/rivoli-ai/andy:1.0@sha256:abcdef0123456789", true)]
    public void IsValid_AcceptsCanonicalRefs(string reference, bool expected)
    {
        OciReferenceValidator.IsValid(reference).Should().Be(expected);
    }

    [Theory]
    // Whitespace — the original threat model.
    [InlineData("evil:latest --rm")]
    [InlineData("evil:latest\n--rm")]
    [InlineData("evil:latest\t-t something")]
    // Shell metacharacters.
    [InlineData("evil:latest;rm -rf /")]
    [InlineData("evil$(whoami):latest")]
    [InlineData("evil`whoami`:latest")]
    [InlineData("evil:latest|cat /etc/passwd")]
    [InlineData("evil:latest&background")]
    [InlineData("evil:latest\\backslash")]
    // Quotes (would never tokenise correctly).
    [InlineData("\"quoted\":latest")]
    [InlineData("'quoted':latest")]
    // Empty / whitespace-only.
    [InlineData("")]
    [InlineData("   ")]
    public void IsValid_RejectsMalformed(string reference)
    {
        OciReferenceValidator.IsValid(reference).Should().BeFalse(
            $"'{reference}' must be rejected as a defense-in-depth check");
    }

    [Fact]
    public void IsValid_NullReturnsFalse()
    {
        OciReferenceValidator.IsValid(null).Should().BeFalse();
    }

    [Fact]
    public void Validate_ValidRef_DoesNotThrow()
    {
        var act = () => OciReferenceValidator.Validate("ubuntu:24.04");
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_Malformed_ThrowsArgumentException_WithDescriptiveMessage()
    {
        var act = () => OciReferenceValidator.Validate("evil:latest --rm");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*not a valid OCI image reference*")
            .Which.Message.Should().Contain("--rm",
                "the error must echo the offending input so operators can self-correct");
    }

    [Fact]
    public void Validate_RespectsParamName()
    {
        // Different call sites use different parameter names; pin that
        // the helper threads the name through so error reporting points
        // at the right binding.
        var act = () => OciReferenceValidator.Validate("evil ;", paramName: "templateBaseImage");

        act.Should().Throw<ArgumentException>()
            .Which.ParamName.Should().Be("templateBaseImage");
    }
}
