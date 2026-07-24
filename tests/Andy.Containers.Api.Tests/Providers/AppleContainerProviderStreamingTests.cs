using System.Text.Json;
using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Providers.Apple;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Api.Tests.Providers;

public sealed class AppleContainerProviderStreamingTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), $"andy-apple-stream-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecStreamingAsync_EmitsCliLinesBeforeProcessExit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_tempDirectory);
        var cliPath = Path.Combine(_tempDirectory, "container-stub");
        await File.WriteAllTextAsync(
            cliPath,
            """
            #!/bin/sh
            printf 'first\n'
            printf 'warning\n' >&2
            sleep 1
            printf 'last\n'
            exit 7
            """);
        File.SetUnixFileMode(
            cliPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var provider = new AppleContainerProvider(
            JsonSerializer.Serialize(new { cliPath }),
            NullLogger<AppleContainerProvider>.Instance);
        var firstLine = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var chunks = new List<ExecOutputChunk>();

        var execTask = provider.ExecStreamingAsync(
            "container-id",
            "agent-runner",
            TimeSpan.FromSeconds(5),
            (chunk, _) =>
            {
                chunks.Add(chunk);
                firstLine.TrySetResult();
                return ValueTask.CompletedTask;
            });

        await firstLine.Task.WaitAsync(TimeSpan.FromSeconds(2));
        execTask.IsCompleted.Should().BeFalse(
            "the first line must be delivered while the CLI process is still running");

        var result = await execTask;

        result.ExitCode.Should().Be(7);
        chunks.Should().Contain(new ExecOutputChunk(ExecStreamKind.Stdout, "first"));
        chunks.Should().Contain(new ExecOutputChunk(ExecStreamKind.Stderr, "warning"));
        chunks.Should().Contain(new ExecOutputChunk(ExecStreamKind.Stdout, "last"));
        result.StdOut.Should().Be("first\nlast");
        result.StdErr.Should().Be("warning");
    }
}
