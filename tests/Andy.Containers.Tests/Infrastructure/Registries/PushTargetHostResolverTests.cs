using Andy.Containers.Infrastructure.Registries.Local;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Infrastructure.Registries;

// Docker Desktop loopback gap (rivoli-ai/andy-containers).
// `docker push localhost:5050/...` runs inside the Docker Desktop VM,
// where localhost is the VM — not the host running zot. These tests
// lock down the rewrite policy:
//   - Docker Desktop  → loopback authority rewritten to host.docker.internal
//   - Linux / default → localhost left unchanged (daemon shares host net)
// The IsDockerDesktopOverride knob makes the OS-platform branch
// deterministic on any CI host.
public class PushTargetHostResolverTests
{
    private static PushTargetHostOptions Options(
        PushTargetHostRewriteMode mode = PushTargetHostRewriteMode.Auto,
        bool? isDockerDesktop = null)
        => new() { Mode = mode, IsDockerDesktopOverride = isDockerDesktop };

    [Fact]
    public void Resolve_DockerDesktop_RewritesLocalhostToHostDockerInternal()
    {
        var result = PushTargetHostResolver.Resolve(
            "localhost:5050", Options(isDockerDesktop: true));

        result.WasRewritten.Should().BeTrue();
        result.TargetAuthority.Should().Be("host.docker.internal:5050");
    }

    [Fact]
    public void Resolve_DockerDesktop_RewritesLoopbackIpToHostDockerInternal()
    {
        var result = PushTargetHostResolver.Resolve(
            "127.0.0.1:5050", Options(isDockerDesktop: true));

        result.WasRewritten.Should().BeTrue();
        result.TargetAuthority.Should().Be("host.docker.internal:5050");
    }

    [Fact]
    public void Resolve_Linux_LeavesLocalhostUnchanged()
    {
        // Auto mode on a Linux/native daemon: localhost in the daemon IS
        // the host, so rewriting would break it.
        var result = PushTargetHostResolver.Resolve(
            "localhost:5050", Options(isDockerDesktop: false));

        result.WasRewritten.Should().BeFalse();
        result.TargetAuthority.Should().Be("localhost:5050");
    }

    [Fact]
    public void Resolve_NeverMode_NeverRewrites_EvenOnDockerDesktop()
    {
        var result = PushTargetHostResolver.Resolve(
            "localhost:5050",
            Options(mode: PushTargetHostRewriteMode.Never, isDockerDesktop: true));

        result.WasRewritten.Should().BeFalse();
        result.TargetAuthority.Should().Be("localhost:5050");
    }

    [Fact]
    public void Resolve_AlwaysMode_RewritesEvenOnLinux()
    {
        var result = PushTargetHostResolver.Resolve(
            "localhost:5050",
            Options(mode: PushTargetHostRewriteMode.Always, isDockerDesktop: false));

        result.WasRewritten.Should().BeTrue();
        result.TargetAuthority.Should().Be("host.docker.internal:5050");
    }

    [Fact]
    public void Resolve_NonLoopbackAuthority_NeverRewritten()
    {
        // A real hostname / LAN IP is already daemon-reachable — leave it.
        var result = PushTargetHostResolver.Resolve(
            "registry.internal:5050", Options(isDockerDesktop: true));

        result.WasRewritten.Should().BeFalse();
        result.TargetAuthority.Should().Be("registry.internal:5050");
    }

    [Fact]
    public void Resolve_AlreadyHostDockerInternal_NotRewrittenAgain()
    {
        var result = PushTargetHostResolver.Resolve(
            "host.docker.internal:5050", Options(isDockerDesktop: true));

        result.WasRewritten.Should().BeFalse();
        result.TargetAuthority.Should().Be("host.docker.internal:5050");
    }

    [Fact]
    public void Resolve_PreservesPort()
    {
        var result = PushTargetHostResolver.Resolve(
            "localhost:9101", Options(isDockerDesktop: true));

        result.TargetAuthority.Should().Be("host.docker.internal:9101");
    }

    [Fact]
    public void Resolve_NoPort_StillRewritesHost()
    {
        var result = PushTargetHostResolver.Resolve(
            "localhost", Options(isDockerDesktop: true));

        result.WasRewritten.Should().BeTrue();
        result.TargetAuthority.Should().Be("host.docker.internal");
    }

    [Fact]
    public void Resolve_CustomAlias_Honored()
    {
        var opts = Options(isDockerDesktop: true);
        opts.DockerDesktopHostAlias = "gateway.docker.internal";

        var result = PushTargetHostResolver.Resolve("localhost:5050", opts);

        result.TargetAuthority.Should().Be("gateway.docker.internal:5050");
        result.AliasHost.Should().Be("gateway.docker.internal");
    }

    [Fact]
    public void ResolveRemoteReference_DockerDesktop_RewritesOnlyAuthority()
    {
        var result = PushTargetHostResolver.ResolveRemoteReference(
            "localhost:5050/foo/bar:v1", Options(isDockerDesktop: true));

        result.WasRewritten.Should().BeTrue();
        result.TargetAuthority.Should().Be("host.docker.internal:5050/foo/bar:v1");
    }

    [Fact]
    public void ResolveRemoteReference_Linux_LeavesRefUnchanged()
    {
        var result = PushTargetHostResolver.ResolveRemoteReference(
            "localhost:5050/foo/bar:v1", Options(isDockerDesktop: false));

        result.WasRewritten.Should().BeFalse();
        result.TargetAuthority.Should().Be("localhost:5050/foo/bar:v1");
    }

    [Fact]
    public void Resolve_RejectsNullOrWhitespaceAuthority()
    {
        var act = () => PushTargetHostResolver.Resolve("  ", Options());
        act.Should().Throw<ArgumentException>();
    }
}
