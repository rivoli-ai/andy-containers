using Andy.Containers.Abstractions.Images;
using Andy.Containers.Infrastructure.Build.Events;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Infrastructure.Build.Events;

// SM.2.7 (rivoli-ai/conductor#2009). Unit tests for
// AsyncBuildExecutor.ClassifyFailure — the internal mapper that
// translates orchestrator error codes to a (BuildFailureReason, transient)
// pair. Every branch (engine launch, registry unreachable, manifest
// unknown, digest mismatch, pull failed, unknown) must be covered so
// regressions in the taxonomy are caught immediately rather than
// surfacing as silent all-retry loops in Conductor.
public class BuildFailureClassifierTests
{
    // ---- Transient branches ----

    [Theory]
    [InlineData("ensure_pull_docker_launch_failed.Pull")]
    [InlineData("ensure_pull_docker_launch_failed.Tag")]
    [InlineData("ensure_pull_docker_launch_failed.Push")]
    [InlineData("ENSURE_PULL_DOCKER_LAUNCH_FAILED.pull")]  // case-insensitive
    public void ClassifyFailure_DockerLaunchFailure_ReturnsEngineUnavailableTransient(string code)
    {
        var (reason, transient) = AsyncBuildExecutor.ClassifyFailure(code);

        reason.Should().Be(BuildFailureReason.EngineUnavailable,
            because: "docker launch failures indicate the engine is unavailable, not a permanent image problem");
        transient.Should().BeTrue(
            because: "a daemon that failed to start may recover; the consumer should retry");
    }

    [Fact]
    public void ClassifyFailure_EngineUnavailableCode_ReturnsEngineUnavailableTransient()
    {
        var (reason, transient) = AsyncBuildExecutor.ClassifyFailure("engine_unavailable");

        reason.Should().Be(BuildFailureReason.EngineUnavailable);
        transient.Should().BeTrue();
    }

    [Theory]
    [InlineData("registry.unreachable")]
    [InlineData("registry.unreachable.dns")]
    [InlineData("ensure_pull_docker_nonzero_exit_1.Pull")]
    [InlineData("ensure_pull_docker_nonzero_exit_28.Push")]  // 28 = curl timeout
    public void ClassifyFailure_RegistryConnectivity_ReturnsRegistryUnreachableTransient(string code)
    {
        var (reason, transient) = AsyncBuildExecutor.ClassifyFailure(code);

        reason.Should().Be(BuildFailureReason.RegistryUnreachable,
            because: "nonzero-exit docker calls are most often connectivity failures");
        transient.Should().BeTrue(
            because: "the registry may recover; Conductor should retry after back-off");
    }

    // ---- Permanent branches ----

    [Theory]
    [InlineData("registry.manifest_unknown")]
    [InlineData("manifest_unknown")]
    [InlineData("image.not-found")]
    public void ClassifyFailure_ManifestUnknown_ReturnsPermanent(string code)
    {
        var (reason, transient) = AsyncBuildExecutor.ClassifyFailure(code);

        reason.Should().Be(BuildFailureReason.ManifestUnknown,
            because: "the tag/repo does not exist; retrying will get the same 404");
        transient.Should().BeFalse();
    }

    [Theory]
    [InlineData("registry.digest_mismatch")]
    [InlineData("digest_mismatch")]
    public void ClassifyFailure_DigestMismatch_ReturnsPermanent(string code)
    {
        var (reason, transient) = AsyncBuildExecutor.ClassifyFailure(code);

        reason.Should().Be(BuildFailureReason.DigestMismatch,
            because: "the registry image differs from the expected digest; retry will pull the same wrong bytes");
        transient.Should().BeFalse();
    }

    [Theory]
    [InlineData("ensure_pull_push_succeeded_but_lookup_failed")]
    [InlineData("image_pull_failed")]
    public void ClassifyFailure_ImagePullFailed_ReturnsPermanent(string code)
    {
        var (reason, transient) = AsyncBuildExecutor.ClassifyFailure(code);

        reason.Should().Be(BuildFailureReason.ImagePullFailed);
        transient.Should().BeFalse();
    }

    // ---- Fallback / edge cases ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ClassifyFailure_NullOrEmpty_ReturnsUnknownPermanent(string? code)
    {
        var (reason, transient) = AsyncBuildExecutor.ClassifyFailure(code);

        reason.Should().Be(BuildFailureReason.Unknown,
            because: "absent codes cannot be classified; surface for operator review");
        transient.Should().BeFalse();
    }

    [Fact]
    public void ClassifyFailure_UnrecognisedCode_ReturnsUnknownPermanent()
    {
        var (reason, transient) = AsyncBuildExecutor.ClassifyFailure("some_future_error_code");

        reason.Should().Be(BuildFailureReason.Unknown);
        transient.Should().BeFalse();
    }
}
