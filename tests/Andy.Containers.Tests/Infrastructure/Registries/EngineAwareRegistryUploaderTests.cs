using Andy.Containers.Infrastructure.Build;
using Andy.Containers.Infrastructure.Registries;
using Andy.Containers.Infrastructure.Registries.Local;
using Andy.Containers.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Tests.Infrastructure.Registries;

// P1F3 (rivoli-ai/andy-containers#276). The composite dispatches to
// the right uploader based on detected engine, caches the choice, and
// surfaces a clear error when no engine is detected.
public class EngineAwareRegistryUploaderTests
{
    public static bool RunsBashStubs => !OperatingSystem.IsWindows();

    [Fact]
    public async Task PushAsync_DispatchesToDocker_WhenEngineIsDockerBuildKit()
    {
        if (!RunsBashStubs) { return; }

        using var dockerStub = new StubScript(exitCode: 0, stderr: "");
        using var appleStub = new StubScript(exitCode: 0, stderr: "");
        var (composite, _) = MakeComposite(BuildEngineKind.DockerBuildKit, dockerStub, appleStub);

        await composite.PushAsync("andy-build:tmp", "localhost:5050/foo:v1", CancellationToken.None);

        dockerStub.Invocations.Should().HaveCount(2,
            "Docker engine selected — DockerCliUploader's tag+push (2 invocations) must run.");
        appleStub.Invocations.Should().BeEmpty(
            "AppleContainersUploader must not run when the engine is Docker.");
    }

    [Fact]
    public async Task PushAsync_DispatchesToApple_WhenEngineIsAppleContainers()
    {
        if (!RunsBashStubs) { return; }

        using var dockerStub = new StubScript(exitCode: 0, stderr: "");
        using var appleStub = new StubScript(exitCode: 0, stderr: "");
        var (composite, _) = MakeComposite(BuildEngineKind.AppleContainers, dockerStub, appleStub);

        await composite.PushAsync("andy-build:tmp", "localhost:5050/foo:v1", CancellationToken.None);

        appleStub.Invocations.Should().HaveCount(2,
            "Apple Containers engine selected — AppleContainersUploader's tag+push (2 invocations) must run.");
        dockerStub.Invocations.Should().BeEmpty(
            "DockerCliUploader must not run when the engine is Apple Containers.");
    }

    [Fact]
    public async Task PushAsync_ThrowsRegistryUploadException_WhenNoEngineDetected()
    {
        using var dockerStub = new StubScript(exitCode: 0, stderr: "");
        using var appleStub = new StubScript(exitCode: 0, stderr: "");
        var (composite, _) = MakeComposite(BuildEngineKind.None, dockerStub, appleStub);

        var act = async () => await composite.PushAsync(
            "andy-build:tmp", "localhost:5050/foo:v1", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RegistryUploadException>();
        ex.Which.Code.Should().Be("EngineAwareRegistryUploader.NoEngine");
    }

    [Fact]
    public async Task PushAsync_CallsDetectorOnce_AcrossRepeatedPushes()
    {
        if (!RunsBashStubs) { return; }

        using var dockerStub = new StubScript(exitCode: 0, stderr: "");
        using var appleStub = new StubScript(exitCode: 0, stderr: "");
        var (composite, detector) = MakeComposite(BuildEngineKind.DockerBuildKit, dockerStub, appleStub);

        await composite.PushAsync("a:tmp", "localhost:5050/a:1", CancellationToken.None);
        await composite.PushAsync("b:tmp", "localhost:5050/b:1", CancellationToken.None);
        await composite.PushAsync("c:tmp", "localhost:5050/c:1", CancellationToken.None);

        detector.Verify(d => d.DetectAsync(It.IsAny<CancellationToken>()), Times.Once,
            "Detection result is cached for the composite's lifetime — three pushes must share one detection.");
    }

    private static (EngineAwareRegistryUploader composite, Mock<IBuildEngineDetector> detector) MakeComposite(
        BuildEngineKind kind, StubScript dockerStub, StubScript appleStub)
    {
        var detector = new Mock<IBuildEngineDetector>();
        detector.Setup(d => d.DetectAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DetectedBuildEngine(kind, kind == BuildEngineKind.None ? string.Empty : "stub", "stub"));

        var docker = new DockerCliUploader(
            NullLogger<DockerCliUploader>.Instance,
            new DockerCliUploaderOptions { DockerExecutablePath = dockerStub.Path });
        var apple = new AppleContainersUploader(
            NullLogger<AppleContainersUploader>.Instance,
            new AppleContainersUploaderOptions { ContainerExecutablePath = appleStub.Path });

        return (new EngineAwareRegistryUploader(detector.Object, docker, apple), detector);
    }
}
