using Andy.Containers.Infrastructure.Registries;
using Andy.Containers.Infrastructure.Registries.Local;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Tests.Infrastructure.Registries;

// IM6 (rivoli-ai/andy-containers#260). DockerCliUploader tests
// substitute the "docker" executable with a stub shell script that
// records its invocation and exits with a configurable code. This
// avoids requiring a real Docker daemon while still exercising the
// real Process.Start machinery: argument quoting, exit-code handling,
// stdout/stderr capture, and the missing-binary failure mode.
//
// macOS / Linux only — the stub is a bash script. Skip on Windows.
public class DockerCliUploaderTests
{
    public static bool RunsBashStubs => !OperatingSystem.IsWindows();

    [Fact]
    public async Task PushAsync_RunsTagThenPushAndCapturesArguments()
    {
        if (!RunsBashStubs) { return; }

        using var stub = new StubScript(exitCode: 0, stderr: "");
        var uploader = MakeUploader(stub);

        await uploader.PushAsync("andy-build:tmp", "localhost:5050/foo:v1", CancellationToken.None);

        stub.Invocations.Should().HaveCount(2,
            "PushAsync runs `docker tag` then `docker push` — two child processes.");
        stub.Invocations[0].Should().Equal(["tag", "andy-build:tmp", "localhost:5050/foo:v1"]);
        stub.Invocations[1].Should().Equal(["push", "localhost:5050/foo:v1"]);
    }

    [Fact]
    public async Task PushAsync_ThrowsWithCapturedOutput_OnNonZeroExit()
    {
        if (!RunsBashStubs) { return; }

        using var stub = new StubScript(
            exitCode: 1,
            stderr: "denied: requested access to the resource is denied");
        var uploader = MakeUploader(stub);

        var act = async () => await uploader.PushAsync(
            "andy-build:tmp", "localhost:5050/foo:v1", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RegistryUploadException>();
        ex.Which.Code.Should().StartWith("DockerCliUploader.Tag.NonZeroExit");
        ex.Which.CapturedOutput.Should().Contain("denied: requested access");
    }

    [Fact]
    public async Task PushAsync_SurfacesLaunchFailureWithStableCode_WhenDockerBinaryMissing()
    {
        if (!RunsBashStubs) { return; }

        var uploader = new DockerCliUploader(
            NullLogger<DockerCliUploader>.Instance,
            new DockerCliUploaderOptions
            {
                DockerExecutablePath = "/nonexistent/path/to/docker-not-installed",
            });

        var act = async () => await uploader.PushAsync(
            "andy-build:tmp", "localhost:5050/foo:v1", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RegistryUploadException>();
        ex.Which.Code.Should().Be("DockerCliUploader.Tag.LaunchFailed",
            "missing-docker is the most common operator pain — the code must be greppable so IM10 can map it to a 503 with an actionable message.");
    }

    private static DockerCliUploader MakeUploader(StubScript stub)
        => new(NullLogger<DockerCliUploader>.Instance,
            new DockerCliUploaderOptions { DockerExecutablePath = stub.Path });
}

/// <summary>
/// Bash script that pretends to be the docker CLI. Records each
/// invocation's arguments to a file and exits with the configured
/// status. Tests read the recorded arguments to assert what the
/// uploader called.
/// </summary>
internal sealed class StubScript : IDisposable
{
    public string Path { get; }
    public string ArgsFile { get; }

    public StubScript(int exitCode, string stderr)
    {
        ArgsFile = System.IO.Path.GetTempFileName();
        Path = System.IO.Path.GetTempFileName();
        // Move to a .sh extension and write the body.
        var shPath = Path + ".sh";
        File.Move(Path, shPath);
        Path = shPath;

        var body = $"""
            #!/usr/bin/env bash
            # Record arguments one-per-line, separated by groups for
            # multi-invocation tests. Each invocation is one ARGS
            # block terminated by an empty line.
            for a in "$@"; do
              echo "$a" >> "{ArgsFile}"
            done
            echo "" >> "{ArgsFile}"
            {(string.IsNullOrEmpty(stderr) ? "" : $"echo '{stderr}' 1>&2")}
            exit {exitCode}
            """;
        File.WriteAllText(Path, body);
        if (!OperatingSystem.IsWindows())
        {
            // Mark the script executable; needed only on Unix —
            // tests early-return on Windows before constructing
            // a StubScript at all.
            File.SetUnixFileMode(Path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    public List<List<string>> Invocations
    {
        get
        {
            if (!File.Exists(ArgsFile))
            {
                return [];
            }
            var lines = File.ReadAllLines(ArgsFile);
            var blocks = new List<List<string>>();
            var current = new List<string>();
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line))
                {
                    if (current.Count > 0)
                    {
                        blocks.Add(current);
                        current = [];
                    }
                }
                else
                {
                    current.Add(line);
                }
            }
            if (current.Count > 0) { blocks.Add(current); }
            return blocks;
        }
    }

    public void Dispose()
    {
        try { File.Delete(Path); } catch { /* best-effort cleanup */ }
        try { File.Delete(ArgsFile); } catch { /* best-effort cleanup */ }
    }
}
