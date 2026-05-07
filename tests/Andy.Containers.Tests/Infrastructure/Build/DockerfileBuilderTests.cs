using Andy.Containers.Abstractions.Images;
using Andy.Containers.Infrastructure.Build;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Infrastructure.Build;

// IM7 (rivoli-ai/andy-containers#261). DockerfileBuilder is a pure
// function over TemplateSpec — same input, same output bytes. These
// tests pin the rendering rules so a future refactor that quietly
// changes layer ordering or escape semantics surfaces here as a
// hash-changing diff (not a 'docker build failed' surprise).
public class DockerfileBuilderTests
{
    [Fact]
    public void Render_ProducesFromBaseImage()
    {
        var spec = MinimalSpec(baseImage: "ubuntu:24.04");

        var dockerfile = DockerfileBuilder.Render(spec);

        dockerfile.Should().Contain("FROM ubuntu:24.04");
    }

    [Fact]
    public void Render_ThrowsWhenBaseImageMissing()
    {
        var spec = new TemplateSpec(
            Code: "t",
            Version: "1.0.0",
            SpecHash: "sha256:x",
            CanonicalJson: "{}");

        var act = () => DockerfileBuilder.Render(spec);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*BaseImage*");
    }

    [Fact]
    public void Render_AptInstallForDebianBase()
    {
        var spec = MinimalSpec(baseImage: "ubuntu:22.04") with
        {
            Packages = ["curl", "git"],
        };

        var dockerfile = DockerfileBuilder.Render(spec);

        dockerfile.Should().Contain("apt-get install");
        dockerfile.Should().Contain("curl");
        dockerfile.Should().Contain("DEBIAN_FRONTEND=noninteractive",
            "non-interactive mode is mandatory in image builds — apt prompts will hang the build forever otherwise.");
        dockerfile.Should().Contain("rm -rf /var/lib/apt/lists",
            "cleaning the apt cache is the standard pattern to keep the image small.");
    }

    [Fact]
    public void Render_ApkInstallForAlpineBase()
    {
        var spec = MinimalSpec(baseImage: "alpine:3.19") with
        {
            Packages = ["bash"],
        };

        var dockerfile = DockerfileBuilder.Render(spec);

        dockerfile.Should().Contain("apk add --no-cache");
        dockerfile.Should().Contain("bash");
        dockerfile.Should().NotContain("apt-get");
    }

    [Theory]
    [InlineData("registry.access.redhat.com/ubi9/ubi", "dnf install")]
    [InlineData("rockylinux:9", "dnf install")]
    [InlineData("almalinux:9", "dnf install")]
    [InlineData("fedora:39", "dnf install")]
    public void DetectPackageManager_RhelFamily(string baseImage, string expectedCmd)
    {
        var spec = MinimalSpec(baseImage) with { Packages = ["git"] };
        var dockerfile = DockerfileBuilder.Render(spec);
        dockerfile.Should().Contain(expectedCmd);
    }

    [Fact]
    public void Render_FilesEachGetTheirOwnCopyLayer()
    {
        var spec = MinimalSpec(baseImage: "ubuntu:24.04") with
        {
            Files = [
                new TemplateFile("a.sh", "/opt/a.sh", Mode: 0b111_101_101 /* 0755 */),
                new TemplateFile("b.txt", "/etc/b.txt"),
            ],
        };

        var dockerfile = DockerfileBuilder.Render(spec);

        dockerfile.Should().Contain("COPY a.sh /opt/a.sh");
        dockerfile.Should().Contain("COPY b.txt /etc/b.txt");
        dockerfile.Should().Contain("chmod 755 /opt/a.sh",
            "mode 0755 should render as octal in the chmod command.");
        // Exactly one chmod line — only the file with an explicit
        // Mode should produce one; the modeless entry is left at
        // whatever the COPY default was.
        var chmodCount = System.Text.RegularExpressions.Regex.Matches(dockerfile, @"\bchmod\b").Count;
        chmodCount.Should().Be(1,
            "exactly one chmod line should appear when one of two files has an explicit mode.");
    }

    [Fact]
    public void Render_InstallCommandsAreOnePerLayer()
    {
        var spec = MinimalSpec(baseImage: "ubuntu:24.04") with
        {
            Install = [
                "npm install -g @anthropic-ai/claude-code",
                "echo 'done'",
            ],
        };

        var dockerfile = DockerfileBuilder.Render(spec);

        dockerfile.Should().Contain("RUN npm install -g @anthropic-ai/claude-code");
        dockerfile.Should().Contain("RUN echo 'done'");
    }

    [Fact]
    public void Render_EntrypointIsExecForm()
    {
        var spec = MinimalSpec(baseImage: "ubuntu:24.04") with
        {
            EntryPoint = "/opt/entrypoint.sh",
        };

        var dockerfile = DockerfileBuilder.Render(spec);

        dockerfile.Should().Contain("ENTRYPOINT [\"/opt/entrypoint.sh\"]",
            "JSON-array exec form avoids the shell-wrapping ambiguity of the string form.");
    }

    [Fact]
    public void Render_MarkersAsLabels()
    {
        var spec = MinimalSpec(baseImage: "ubuntu:24.04") with
        {
            Markers = new Dictionary<string, IReadOnlyList<string>>
            {
                ["baked-assistants"] = new List<string> { "claude-code", "opencode" },
            },
        };

        var dockerfile = DockerfileBuilder.Render(spec);

        dockerfile.Should().Contain("LABEL ai.rivoli.andy-containers.markers.baked-assistants=");
        dockerfile.Should().Contain("claude-code,opencode",
            "marker value lists are comma-joined for the LABEL value.");
    }

    [Fact]
    public void Render_IsStableForTheSameSpec()
    {
        var spec = FullSpec();

        var first = DockerfileBuilder.Render(spec);
        var second = DockerfileBuilder.Render(spec);

        second.Should().Be(first,
            "the renderer is a pure function; same input → same bytes (so the spec hash remains a meaningful cache key).");
    }

    [Fact]
    public void Render_OrdersMarkersDeterministically()
    {
        var first = DockerfileBuilder.Render(MinimalSpec("ubuntu:24.04") with
        {
            Markers = new Dictionary<string, IReadOnlyList<string>>
            {
                ["zzz"] = ["last"],
                ["aaa"] = ["first"],
            },
        });
        var second = DockerfileBuilder.Render(MinimalSpec("ubuntu:24.04") with
        {
            Markers = new Dictionary<string, IReadOnlyList<string>>
            {
                ["aaa"] = ["first"],
                ["zzz"] = ["last"],
            },
        });

        second.Should().Be(first,
            "marker iteration order shouldn't change the Dockerfile — sort lexicographically.");

        var aaaIndex = first.IndexOf("aaa", StringComparison.Ordinal);
        var zzzIndex = first.IndexOf("zzz", StringComparison.Ordinal);
        aaaIndex.Should().BeLessThan(zzzIndex);
    }

    [Fact]
    public void Render_QuotesArgumentsContainingShellMetacharacters()
    {
        var spec = MinimalSpec(baseImage: "ubuntu:24.04") with
        {
            Files = [new TemplateFile("file with spaces.sh", "/path/with spaces/dest")],
        };

        var dockerfile = DockerfileBuilder.Render(spec);

        // The quoting must be such that the shell can resolve the
        // argument as a single token, not split on whitespace.
        dockerfile.Should().Contain("'file with spaces.sh'");
        dockerfile.Should().Contain("'/path/with spaces/dest'");
    }

    private static TemplateSpec MinimalSpec(string baseImage)
        => new(
            Code: "t",
            Version: "1.0.0",
            SpecHash: "sha256:0",
            CanonicalJson: "{}")
        {
            BaseImage = baseImage,
        };

    private static TemplateSpec FullSpec()
        => new(
            Code: "conductor-terminal-claude-code",
            Version: "1.0.0",
            SpecHash: "sha256:abc",
            CanonicalJson: "{}")
        {
            BaseImage = "ubuntu:22.04",
            Packages = ["curl", "ca-certificates"],
            Files = [
                new TemplateFile("install-assistants.sh", "/opt/conductor/install-assistants.sh", Mode: 0b111_101_101),
            ],
            Install = ["npm install -g @anthropic-ai/claude-code"],
            EntryPoint = "/opt/conductor/entrypoint.sh",
            Markers = new Dictionary<string, IReadOnlyList<string>>
            {
                ["baked-assistants"] = new List<string> { "claude-code" },
            },
        };
}
