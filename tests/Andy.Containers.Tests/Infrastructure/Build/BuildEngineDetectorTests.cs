using Andy.Containers.Infrastructure.Build;
using Andy.Containers.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Tests.Infrastructure.Build;

// IM7 (rivoli-ai/andy-containers#261). Engine-detection probes shell
// out to the candidate binaries; the test substitutes them with bash
// stubs that exit 0 (engine present) or with paths that don't exist
// (engine absent). The bash stub trick reuses the StubScript helper
// from the IM6 DockerCliUploader tests.
//
// Tests early-return on Windows since the stub is a bash script.
public class BuildEngineDetectorTests
{
    public static bool RunsBashStubs => !OperatingSystem.IsWindows();

    [Fact]
    public async Task Detect_ChoosesAppleContainers_WhenAvailable()
    {
        if (!RunsBashStubs) { return; }

        using var apple = MakeStubBinary(exitCode: 0, stdoutLine: "container 1.2.3");
        using var docker = MakeStubBinary(exitCode: 0, stdoutLine: "Docker buildx 0.13.0");

        var detector = MakeDetector(apple, docker);

        var result = await detector.DetectAsync(CancellationToken.None);

        result.Kind.Should().Be(BuildEngineKind.AppleContainers,
            "Apple Containers takes precedence when both are available — that's the documented IM1 priority.");
        result.ExecutablePath.Should().Be(apple.Path);
        result.ProbedVersion.Should().Be("container 1.2.3");
    }

    [Fact]
    public async Task Detect_FallsBackToDockerBuildKit_WhenAppleAbsent()
    {
        if (!RunsBashStubs) { return; }

        using var docker = MakeStubBinary(exitCode: 0, stdoutLine: "buildx v0.13.0");

        var detector = MakeDetector(
            appleContainerPath: "/nonexistent/container",
            dockerStub: docker);

        var result = await detector.DetectAsync(CancellationToken.None);

        result.Kind.Should().Be(BuildEngineKind.DockerBuildKit);
        result.ExecutablePath.Should().Be(docker.Path);
        result.ProbedVersion.Should().Be("buildx v0.13.0");
    }

    [Fact]
    public async Task Detect_ReturnsNone_WhenNeitherEngineIsAvailable()
    {
        if (!RunsBashStubs) { return; }

        var detector = new BuildEngineDetector(
            NullLogger<BuildEngineDetector>.Instance,
            new BuildEngineDetectorOptions
            {
                AppleContainerPath = "/nonexistent/container",
                DockerPath = "/nonexistent/docker",
            });

        var result = await detector.DetectAsync(CancellationToken.None);

        result.Kind.Should().Be(BuildEngineKind.None);
        result.ExecutablePath.Should().BeEmpty();
    }

    [Fact]
    public async Task Detect_TreatsNonZeroExitAsEngineAbsent()
    {
        if (!RunsBashStubs) { return; }

        // Stub binaries exit 1 — the binary exists on PATH but
        // the probe failed. Treat as not-available rather than
        // surfacing the exit as an unrelated failure.
        using var failingApple = MakeStubBinary(exitCode: 1, stdoutLine: "");
        using var failingDocker = MakeStubBinary(exitCode: 1, stdoutLine: "");

        var detector = MakeDetector(failingApple, failingDocker);

        var result = await detector.DetectAsync(CancellationToken.None);

        result.Kind.Should().Be(BuildEngineKind.None);
    }

    [Fact]
    public async Task Detect_ResultIsCachedAcrossCalls()
    {
        if (!RunsBashStubs) { return; }

        using var apple = MakeStubBinary(exitCode: 0, stdoutLine: "container 1.0");

        var detector = MakeDetector(apple, dockerStub: null);

        var first = await detector.DetectAsync(CancellationToken.None);
        var second = await detector.DetectAsync(CancellationToken.None);

        // Both invocations should yield the same instance — caching
        // means the second call doesn't reprobe.
        first.Should().BeSameAs(second);
        // And the apple binary was only invoked once.
        apple.Invocations.Should().HaveCount(1,
            "the detector caches the first result; subsequent DetectAsync calls must not reprobe.");
    }

    private static BuildEngineDetector MakeDetector(
        StubScript? appleStub,
        StubScript? dockerStub)
        => MakeDetector(
            appleContainerPath: appleStub?.Path ?? "/nonexistent/container",
            dockerStub: dockerStub);

    private static BuildEngineDetector MakeDetector(
        string appleContainerPath,
        StubScript? dockerStub)
        => new(
            NullLogger<BuildEngineDetector>.Instance,
            new BuildEngineDetectorOptions
            {
                AppleContainerPath = appleContainerPath,
                DockerPath = dockerStub?.Path ?? "/nonexistent/docker",
                ProbeTimeout = TimeSpan.FromSeconds(2),
            });

    private static StubScript MakeStubBinary(int exitCode, string stdoutLine)
    {
        // Variant of the IM6 StubScript with stdout output instead
        // of stderr. We can't reuse that one because we need to emit
        // a version line on stdout for the detector to read.
        return new StubScript(
            exitCode: exitCode,
            stdoutLine: stdoutLine,
            stderr: "");
    }
}
