using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Providers.Shared;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Providers;

// rivoli-ai/andy-containers#128. The pre-fix code interpolated env
// values, image refs, command, and args into a single shell-line that
// got executed on a remote sshd. Whitespace or shell metacharacters in
// any value would split into independent argv tokens or trigger
// metacharacter expansion. BuildRunCommand now POSIX-quotes every
// interpolated value; these tests pin the shape of the assembled
// command so a future regression breaks here, not in the field.
public class SshDockerHelperBuildRunTests
{
    [Fact]
    public void Build_BareMinimum_ProducesQuotedDockerRun()
    {
        var spec = new ContainerSpec { Name = "x", ImageReference = "ubuntu:24.04" };

        var cmd = SshDockerHelper.BuildRunCommand(spec, "andy-abc123");

        cmd.Should().StartWith("docker run -d --name 'andy-abc123'",
            "container name lands quoted even though Guid-derived names are safe today");
        cmd.Should().Contain(" 'ubuntu:24.04'",
            "image reference is the primary untrusted value — always quoted");
        cmd.Should().Contain("--pids-limit 4096",
            "the hardening block carries through unchanged");
        cmd.Should().Contain("--security-opt no-new-privileges");
        cmd.Should().Contain("--cap-drop NET_RAW");
    }

    [Fact]
    public void Build_EnvValueWithSpace_LandsInOneQuotedSlot()
    {
        // The threat model: a template-author-controlled env value
        // with a space would have split into two argv tokens at the
        // remote shell under the old code.
        var spec = new ContainerSpec
        {
            Name = "x",
            ImageReference = "ubuntu:24.04",
            EnvironmentVariables = new() { ["GREETING"] = "hello world" },
        };

        var cmd = SshDockerHelper.BuildRunCommand(spec, "n");

        cmd.Should().Contain(" -e 'GREETING=hello world'",
            "the entire KEY=VALUE pair is one quoted slot — space stays inside the quotes");
        cmd.Should().NotContain(" world ", "the value must not split into 'world' as an extra token");
    }

    [Fact]
    public void Build_EnvValueWithDollarAndBackticks_DoesNotExpand()
    {
        var spec = new ContainerSpec
        {
            Name = "x",
            ImageReference = "ubuntu:24.04",
            EnvironmentVariables = new() { ["TOKEN"] = "$(whoami)`id`" },
        };

        var cmd = SshDockerHelper.BuildRunCommand(spec, "n");

        cmd.Should().Contain(" -e 'TOKEN=$(whoami)`id`'",
            "single quotes neuter $ and ` — the metacharacters reach the docker daemon as literal bytes");
    }

    [Fact]
    public void Build_EnvValueWithSingleQuote_UsesEscapeSequence()
    {
        var spec = new ContainerSpec
        {
            Name = "x",
            ImageReference = "ubuntu:24.04",
            EnvironmentVariables = new() { ["NAME"] = "O'Malley" },
        };

        var cmd = SshDockerHelper.BuildRunCommand(spec, "n");

        cmd.Should().Contain("'NAME=O'\\''Malley'",
            "embedded single quote uses the canonical close-escape-reopen pattern");
    }

    [Fact]
    public void Build_CommandWithArguments_QuotesEachIndependently()
    {
        var spec = new ContainerSpec
        {
            Name = "x",
            ImageReference = "ubuntu:24.04",
            Command = "/usr/bin/python3",
            Arguments = new[] { "-c", "print('hi'); import os; os.system('id')" },
        };

        var cmd = SshDockerHelper.BuildRunCommand(spec, "n");

        cmd.Should().Contain(" '/usr/bin/python3'");
        cmd.Should().Contain(" '-c'");
        cmd.Should().Contain(
            "'print('\\''hi'\\''); import os; os.system('\\''id'\\'')'",
            "embedded quotes round-trip via the close-escape-reopen pattern");
    }

    [Fact]
    public void Build_ImageRefWithEmbeddedFlag_StaysOneQuotedSlot()
    {
        // OciReferenceValidator rejects this at the entry, but
        // BuildRunCommand is also called by tests / future callers
        // that may not validate. Pin that the quoting holds even when
        // the validator hasn't run.
        var spec = new ContainerSpec
        {
            Name = "x",
            ImageReference = "evil:latest --rm --volume /:/host",
        };

        var cmd = SshDockerHelper.BuildRunCommand(spec, "n");

        cmd.Should().Contain(" 'evil:latest --rm --volume /:/host'",
            "even adversarial image refs must reach the daemon as one quoted argv slot");
        // Verify there's no UNQUOTED occurrence of `--rm`. The string
        // search would match inside the quoted blob, so split on the
        // single quote that opens the image ref and assert nothing
        // before that point carries `--rm`.
        var openQuote = cmd.IndexOf(" 'evil:", StringComparison.Ordinal);
        openQuote.Should().BeGreaterThan(0);
        cmd[..openQuote].Should().NotContain("--rm",
            "outside the quoted image ref, `--rm` must not appear as an independent flag");
    }

    [Fact]
    public void Build_PortMappings_ProduceQuotedDashP()
    {
        var spec = new ContainerSpec
        {
            Name = "x",
            ImageReference = "ubuntu:24.04",
            PortMappings = new() { [8080] = 12345 },
        };

        var cmd = SshDockerHelper.BuildRunCommand(spec, "n");

        cmd.Should().Contain(" -p '12345:8080'");
    }

    [Fact]
    public void Build_NullSpec_Throws()
    {
        var act = () => SshDockerHelper.BuildRunCommand(null!, "n");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Build_BlankContainerName_Throws()
    {
        var act = () => SshDockerHelper.BuildRunCommand(
            new ContainerSpec { Name = "x", ImageReference = "ubuntu:24.04" }, "");
        act.Should().Throw<ArgumentException>();
    }
}
