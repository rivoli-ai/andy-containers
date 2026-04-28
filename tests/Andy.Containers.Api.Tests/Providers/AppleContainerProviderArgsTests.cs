using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Providers.Apple;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Providers;

// rivoli-ai/andy-containers#127. The pre-fix code built the entire
// `container run ...` line as a string and let .NET's Win32 parser
// tokenise it. A template's BaseImage with a space, an env value with
// whitespace, or a Command with embedded flags would split into extra
// CLI tokens and let a caller with template:write smuggle in flags
// (`--rm`, `--volume`, etc.). These tests pin the contract that every
// untrusted value lands in exactly one argv slot regardless of its
// content.
public class AppleContainerProviderArgsTests
{
    [Fact]
    public void Build_BareMinimum_StartsWithRunNameAndAddsDefaults()
    {
        var args = AppleContainerProvider.BuildCreateContainerArgs(
            new ContainerSpec { Name = "x", ImageReference = "ubuntu:24.04" },
            "x");

        args.Should().StartWith(new[] { "run", "--name", "x" });
        args.Should().EndWith(new[] { "-d", "ubuntu:24.04", "sleep", "infinity" });
    }

    [Fact]
    public void Build_EnvValueWithSpaces_LandsInSingleArgvSlot()
    {
        // The previous string-build path skipped this with a warning;
        // here it must pass through cleanly because each env var becomes
        // its own argv element. Use a value that would have tokenised
        // into three pieces under the old parser.
        var spec = new ContainerSpec
        {
            Name = "x",
            ImageReference = "ubuntu:24.04",
            EnvironmentVariables = new() { ["GREETING"] = "hello world from apple" },
        };

        var args = AppleContainerProvider.BuildCreateContainerArgs(spec, "x");

        args.Should().Contain("-e");
        args.Should().Contain("GREETING=hello world from apple",
            "spaces in env values are now safe — the value is one argv slot");
    }

    [Fact]
    public void Build_EnvValueWithQuoteAndDollar_PassesThroughVerbatim()
    {
        // The argv path also handles characters a shell would expand.
        var spec = new ContainerSpec
        {
            Name = "x",
            ImageReference = "ubuntu:24.04",
            EnvironmentVariables = new() { ["KEY"] = "p\"a$ssword" },
        };

        var args = AppleContainerProvider.BuildCreateContainerArgs(spec, "x");

        args.Should().Contain("KEY=p\"a$ssword");
    }

    [Fact]
    public void Build_ImageReferenceWithEmbeddedFlag_StaysOneArgvElement()
    {
        // Threat model: a template author sets BaseImage to something
        // that would have tokenised into two args under the old path.
        // The Apple CLI will reject this as an unknown image — that's
        // fine; the point is that "--rm" never reaches argv as its own
        // slot.
        var spec = new ContainerSpec
        {
            Name = "x",
            ImageReference = "evil:latest --rm --volume /:/host",
        };

        var args = AppleContainerProvider.BuildCreateContainerArgs(spec, "x");

        // Find the `-d` token; the very next slot must be the entire
        // (malicious) image string, with no extra slots in between.
        var dashD = Array.IndexOf(args, "-d");
        dashD.Should().BeGreaterThanOrEqualTo(0);
        args[dashD + 1].Should().Be("evil:latest --rm --volume /:/host",
            "the whole image-reference string is one argv slot; --rm and --volume " +
            "do not become independent CLI flags");
        args.Should().NotContain("--rm");
        args.Should().NotContain("--volume");
    }

    [Fact]
    public void Build_CommandWithArguments_AppendsInOrder_AfterImage()
    {
        var spec = new ContainerSpec
        {
            Name = "x",
            ImageReference = "ubuntu:24.04",
            Command = "/usr/bin/python3",
            Arguments = new[] { "-c", "print('hi from $USER')" },
        };

        var args = AppleContainerProvider.BuildCreateContainerArgs(spec, "x");

        var dashD = Array.IndexOf(args, "-d");
        args[dashD + 1].Should().Be("ubuntu:24.04");
        args[dashD + 2].Should().Be("/usr/bin/python3");
        args[dashD + 3].Should().Be("-c");
        args[dashD + 4].Should().Be("print('hi from $USER')",
            "argument with single quotes + dollar passes through as one slot");
        args.Should().NotContain("sleep",
            "the default keep-alive command is replaced when an explicit Command is set");
    }

    [Fact]
    public void Build_Resources_ProducesSeparateArgvSlots()
    {
        var spec = new ContainerSpec
        {
            Name = "x",
            ImageReference = "ubuntu:24.04",
            Resources = new ResourceSpec { CpuCores = 4, MemoryMb = 2048, DiskGb = 0 },
        };

        var args = AppleContainerProvider.BuildCreateContainerArgs(spec, "x");

        // -c must be followed by the count as an int (invariant culture
        // — a fr-FR runtime would render it with a comma otherwise).
        var dashC = Array.IndexOf(args, "-c");
        args[dashC + 1].Should().Be("4");
        var dashM = Array.IndexOf(args, "-m");
        args[dashM + 1].Should().Be("2048M");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void Build_OmitsResourceFlagsWhenZeroOrUnset(int cpu, bool expectFlag)
    {
        var spec = new ContainerSpec
        {
            Name = "x",
            ImageReference = "ubuntu:24.04",
            Resources = new ResourceSpec { CpuCores = cpu, MemoryMb = 0, DiskGb = 0 },
        };

        var args = AppleContainerProvider.BuildCreateContainerArgs(spec, "x");

        args.Contains("-c").Should().Be(expectFlag);
        args.Contains("-m").Should().BeFalse("MemoryMb=0 means use the runtime default");
    }

    [Fact]
    public void Build_NullSpec_Throws()
    {
        var act = () => AppleContainerProvider.BuildCreateContainerArgs(null!, "x");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Build_BlankName_Throws()
    {
        // Container name is the one value the operator owns; refuse to
        // build args for an empty one rather than producing nonsense the
        // CLI would reject seconds later.
        var act = () => AppleContainerProvider.BuildCreateContainerArgs(
            new ContainerSpec { Name = "x", ImageReference = "ubuntu:24.04" },
            "");
        act.Should().Throw<ArgumentException>();
    }
}
