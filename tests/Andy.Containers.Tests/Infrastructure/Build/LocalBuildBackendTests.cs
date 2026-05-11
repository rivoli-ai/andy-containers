using Andy.Containers.Abstractions.Images;
using Andy.Containers.Infrastructure.Build;
using Andy.Containers.Infrastructure.Build.Local;
using Andy.Containers.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Tests.Infrastructure.Build;

// IM7 (rivoli-ai/andy-containers#261). Backend orchestration tests:
// the engine detector is mocked so we control which engine is
// "available," and the actual engine invocation is a bash stub that
// records its arguments and writes canned progress output.
public class LocalBuildBackendTests
{
    public static bool RunsBashStubs => !OperatingSystem.IsWindows();

    [Fact]
    public async Task BuildAsync_ThrowsImageBuildFailed_WhenNoEngineAvailable()
    {
        var detector = new Mock<IBuildEngineDetector>();
        detector.Setup(d => d.DetectAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DetectedBuildEngine(BuildEngineKind.None, string.Empty, string.Empty));

        var backend = new LocalBuildBackend(detector.Object, NullLogger<LocalBuildBackend>.Instance);
        var spec = MakeSpec();
        var ctx = new EmptyBuildContext("/tmp");
        var progress = new Progress<BuildProgressEvent>(_ => { });

        var act = async () => await backend.BuildAsync(spec, ctx, progress, CancellationToken.None);

        await act.Should().ThrowAsync<ImageBuildFailedException>()
            .Where(e => e.FailingStepName == "engine-detect"
                     && e.Message.Contains("install Apple Containers", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildAsync_StagesContextRendersDockerfileAndInvokesEngine()
    {
        if (!RunsBashStubs) { return; }

        using var stub = new StubScript(exitCode: 0, stdoutLine: "Successfully built abcd1234", stderr: "");
        var detector = new Mock<IBuildEngineDetector>();
        detector.Setup(d => d.DetectAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DetectedBuildEngine(BuildEngineKind.DockerBuildKit, stub.Path, "stub"));

        var backend = new LocalBuildBackend(
            detector.Object,
            NullLogger<LocalBuildBackend>.Instance,
            new LocalBuildBackendOptions { PreserveBuildContext = true });

        var spec = MakeSpec();
        var ctx = new EmptyBuildContext("/tmp");
        var events = new List<BuildProgressEvent>();
        var progress = new Progress<BuildProgressEvent>(events.Add);

        var artifact = await backend.BuildAsync(spec, ctx, progress, CancellationToken.None);

        artifact.SpecHash.Should().Be(spec.SpecHash);
        artifact.LocalReference.Should().StartWith("andy-containers-build-",
            "the build backend assigns a unique local tag for each build so the registry uploader has a stable handle.");

        // Wait briefly for the Progress<T> SynchronizationContext to flush.
        await Task.Delay(100);
        events.OfType<BuildStepStartedEvent>().Should().ContainSingle();
        events.OfType<BuildCompletedEvent>().Should().ContainSingle()
            .Which.Outcome.Should().Be(BuildOutcome.Succeeded);
        events.OfType<BuildStepStdoutEvent>().Select(e => e.Line)
            .Should().Contain("Successfully built abcd1234",
                "stdout from the engine should reach the caller as BuildStepStdoutEvent.");

        // Verify the engine was invoked with the docker buildx flavour.
        stub.Invocations.Should().ContainSingle();
        stub.Invocations[0].Should().Equal(
            ["buildx", "build", "--load", "--provenance=false", "--sbom=false",
             "-t", artifact.LocalReference, "-f", "Dockerfile", "."],
            "BuildKit must strip provenance + SBOM attestations so zot accepts the manifest (see #275).");
    }

    [Fact]
    public async Task BuildAsync_AppleContainersFlavourArguments()
    {
        if (!RunsBashStubs) { return; }

        using var stub = new StubScript(exitCode: 0, stdoutLine: "ok", stderr: "");
        var detector = new Mock<IBuildEngineDetector>();
        detector.Setup(d => d.DetectAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DetectedBuildEngine(BuildEngineKind.AppleContainers, stub.Path, "stub"));

        var backend = new LocalBuildBackend(detector.Object, NullLogger<LocalBuildBackend>.Instance);

        var artifact = await backend.BuildAsync(
            MakeSpec(), new EmptyBuildContext("/tmp"),
            new Progress<BuildProgressEvent>(_ => { }), CancellationToken.None);

        // Apple Containers takes `build` directly without a `buildx`
        // subcommand and doesn't use --load.
        stub.Invocations[0].Should().Equal(["build", "-t", artifact.LocalReference, "-f", "Dockerfile", "."]);
    }

    [Fact]
    public async Task BuildAsync_NonZeroExit_ThrowsWithCapturedLogs()
    {
        if (!RunsBashStubs) { return; }

        using var stub = new StubScript(
            exitCode: 1,
            stdoutLine: "doing things",
            stderr: "ERROR: failed to fetch package");
        var detector = new Mock<IBuildEngineDetector>();
        detector.Setup(d => d.DetectAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DetectedBuildEngine(BuildEngineKind.DockerBuildKit, stub.Path, "stub"));

        var backend = new LocalBuildBackend(detector.Object, NullLogger<LocalBuildBackend>.Instance);

        var events = new List<BuildProgressEvent>();
        var progress = new Progress<BuildProgressEvent>(events.Add);

        var act = async () => await backend.BuildAsync(
            MakeSpec(), new EmptyBuildContext("/tmp"), progress, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ImageBuildFailedException>();
        ex.Which.FailingStepName.Should().Be("build");
        ex.Which.CapturedLogs.Should().Contain("ERROR: failed to fetch package",
            "stderr is captured into ImageBuildFailedException.CapturedLogs so IM10 can include it in the 422 response.");

        // Failure path also emits a completion event.
        await Task.Delay(100);
        events.OfType<BuildCompletedEvent>().Should().ContainSingle()
            .Which.Outcome.Should().Be(BuildOutcome.Failed);
    }

    [Fact]
    public void Capabilities_ReportsHostArchitecture()
    {
        var detector = new Mock<IBuildEngineDetector>();
        var backend = new LocalBuildBackend(detector.Object, NullLogger<LocalBuildBackend>.Instance);

        var caps = backend.Capabilities;

        caps.SupportedArchitectures.Should().NotBeEmpty();
        caps.SupportsCacheImport.Should().BeTrue();
        caps.SupportsMultiArch.Should().BeFalse(
            "IM7 ships single-arch builds; multi-arch is a follow-up.");
    }

    private static TemplateSpec MakeSpec()
        => new(
            Code: "test-build",
            Version: "1.0.0",
            SpecHash: "sha256:test",
            CanonicalJson: "{}")
        {
            BaseImage = "ubuntu:24.04",
            Install = ["echo hello"],
        };

    private sealed class EmptyBuildContext : IBuildContext
    {
        public EmptyBuildContext(string contextDir) { ContextDirectoryPath = contextDir; }
        public string ContextDirectoryPath { get; }
        public IReadOnlyList<UploadedFile> Files => Array.Empty<UploadedFile>();
    }
}
