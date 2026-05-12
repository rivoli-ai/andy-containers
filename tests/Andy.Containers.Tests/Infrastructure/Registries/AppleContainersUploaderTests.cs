using Andy.Containers.Infrastructure.Registries;
using Andy.Containers.Infrastructure.Registries.Local;
using Andy.Containers.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Tests.Infrastructure.Registries;

// P1F3 (rivoli-ai/andy-containers#276). Mirrors DockerCliUploaderTests
// — the `container` executable is substituted with a bash stub that
// records its invocation. macOS / Linux only.
public class AppleContainersUploaderTests
{
    public static bool RunsBashStubs => !OperatingSystem.IsWindows();

    [Fact]
    public async Task PushAsync_RunsImagesTagThenImagesPushAndCapturesArguments()
    {
        if (!RunsBashStubs) { return; }

        using var stub = new StubScript(exitCode: 0, stderr: "");
        var uploader = MakeUploader(stub);

        await uploader.PushAsync("andy-build:tmp", "localhost:5050/foo:v1", CancellationToken.None);

        stub.Invocations.Should().HaveCount(2,
            "PushAsync runs `container images tag` then `container images push` — two child processes.");
        stub.Invocations[0].Should().Equal(["images", "tag", "andy-build:tmp", "localhost:5050/foo:v1"]);
        stub.Invocations[1].Should().Equal(["images", "push", "localhost:5050/foo:v1"]);
    }

    [Fact]
    public async Task PushAsync_ThrowsWithCapturedOutput_OnNonZeroExit()
    {
        if (!RunsBashStubs) { return; }

        using var stub = new StubScript(
            exitCode: 1,
            stderr: "Error: manifest invalid: provenance attestation rejected");
        var uploader = MakeUploader(stub);

        var act = async () => await uploader.PushAsync(
            "andy-build:tmp", "localhost:5050/foo:v1", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RegistryUploadException>();
        ex.Which.Code.Should().StartWith("AppleContainersUploader.Tag.NonZeroExit");
        ex.Which.CapturedOutput.Should().Contain("manifest invalid");
    }

    [Fact]
    public async Task PushAsync_SurfacesLaunchFailureWithStableCode_WhenContainerBinaryMissing()
    {
        if (!RunsBashStubs) { return; }

        var uploader = new AppleContainersUploader(
            NullLogger<AppleContainersUploader>.Instance,
            new AppleContainersUploaderOptions
            {
                ContainerExecutablePath = "/nonexistent/path/to/container-not-installed",
            });

        var act = async () => await uploader.PushAsync(
            "andy-build:tmp", "localhost:5050/foo:v1", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RegistryUploadException>();
        ex.Which.Code.Should().Be("AppleContainersUploader.Tag.LaunchFailed",
            "missing `container` CLI is the macOS-26+-without-runtime operator pain — the code must be greppable so IM10 maps it to a 503 with an actionable message.");
    }

    private static AppleContainersUploader MakeUploader(StubScript stub)
        => new(NullLogger<AppleContainersUploader>.Instance,
            new AppleContainersUploaderOptions { ContainerExecutablePath = stub.Path });
}
