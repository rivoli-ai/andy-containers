using Andy.Containers.Api.Services;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public sealed class HeadlessRunCacheReclaimerTests
{
    [Fact]
    public void BuildReclamationCommand_RemovesOnlyOldInactiveRunDirectories_AndIsIdempotent()
    {
        var root = Directory.CreateTempSubdirectory("andy-run-cache-reclaim-").FullName;
        try
        {
            var orphan = Path.Combine(root, Guid.NewGuid().ToString());
            var active = Path.Combine(root, Guid.NewGuid().ToString());
            var unrelated = Path.Combine(root, "user-data");
            Directory.CreateDirectory(orphan);
            Directory.CreateDirectory(active);
            Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(orphan, ".owner-pid"), "999999999");
            File.WriteAllText(Path.Combine(orphan, "package.bin"), "orphan");
            File.WriteAllText(Path.Combine(active, ".owner-pid"), Environment.ProcessId.ToString());
            File.WriteAllText(Path.Combine(active, "package.bin"), "active");
            File.WriteAllText(Path.Combine(unrelated, "notes.txt"), "unrelated");

            var old = DateTime.UtcNow.AddDays(-1);
            Directory.SetLastWriteTimeUtc(orphan, old);
            Directory.SetLastWriteTimeUtc(active, old);
            Directory.SetLastWriteTimeUtc(unrelated, old);

            var command = HeadlessRunCacheReclaimer.BuildReclamationCommand(
                root, TimeSpan.FromMinutes(1));
            var first = RunShell(command);

            first.ExitCode.Should().Be(0, first.StdErr);
            Directory.Exists(orphan).Should().BeFalse();
            Directory.Exists(active).Should().BeTrue("its owner PID is still alive");
            Directory.Exists(unrelated).Should().BeTrue("only GUID-scoped run directories are owned by the reclaimer");
            first.StdOut.Should().Contain("[AC-RUN-CACHE-RECLAIMED]");

            var second = RunShell(command);
            second.ExitCode.Should().Be(0, second.StdErr);
            Directory.Exists(active).Should().BeTrue();
            Directory.Exists(unrelated).Should().BeTrue();
            second.StdOut.Should().NotContain("[AC-RUN-CACHE-RECLAIMED]");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProcessResult RunShell(string command)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start sh.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
