using System.Text.Json;
using Andy.Containers.Api.Services;
using Andy.Containers.Configurator;
using FluentAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Andy.Containers.Api.Tests.Configurator;

// AP3 (rivoli-ai/andy-containers#105). Verifies the writer atomically
// produces a snake_case JSON file under the embedded path. The hosted path
// (/var/run/andy/runs) is not exercised here — it requires root on the
// CI host and the path-selection branch is covered by HostEnvironment.
public class HeadlessConfigWriterTests : IDisposable
{
    private readonly string _tempBase;

    public HeadlessConfigWriterTests()
    {
        _tempBase = Path.Combine(Path.GetTempPath(), "andy-containers", "runs");
    }

    public void Dispose()
    {
        // Tests above write under the OS temp dir. Best-effort cleanup of
        // run subdirs created during this run; intentionally narrow so we
        // never blow away unrelated state if Path.GetTempPath shifts.
        if (!Directory.Exists(_tempBase)) return;
        foreach (var dir in Directory.EnumerateDirectories(_tempBase))
        {
            if (Guid.TryParse(Path.GetFileName(dir), out _))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public async Task WriteAsync_Embedded_WritesSnakeCaseJsonUnderTempRoot()
    {
        var writer = new HeadlessConfigWriter(new FakeEnv(HostEnvironmentExtensions.EmbeddedEnvironmentName));
        var config = SampleConfig();

        var path = await writer.WriteAsync(config);

        path.Should().StartWith(_tempBase);
        File.Exists(path).Should().BeTrue();

        var json = await File.ReadAllTextAsync(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("schema_version").GetInt32().Should().Be(1);
        root.GetProperty("run_id").GetGuid().Should().Be(config.RunId);
        root.GetProperty("agent").GetProperty("slug").GetString().Should().Be("triage-agent");
        root.GetProperty("model").GetProperty("provider").GetString().Should().Be("anthropic");
        root.TryGetProperty("policy_id", out _).Should().BeFalse(
            "null optional fields are skipped via DefaultIgnoreCondition.WhenWritingNull");
    }

    [Fact]
    public async Task WriteAsync_OverwritesExistingFile()
    {
        var writer = new HeadlessConfigWriter(new FakeEnv(HostEnvironmentExtensions.EmbeddedEnvironmentName));
        var config = SampleConfig();

        var first = await writer.WriteAsync(config);
        var updated = config with { Boundaries = new[] { "read-only" } };
        var second = await writer.WriteAsync(updated);

        second.Should().Be(first, "same RunId always resolves to the same on-disk path");
        var json = await File.ReadAllTextAsync(second);
        json.Should().Contain("read-only", "second write replaces the first atomically");
    }

    [Fact]
    public async Task WriteAsync_LeavesNoTmpFileBehind()
    {
        var writer = new HeadlessConfigWriter(new FakeEnv(HostEnvironmentExtensions.EmbeddedEnvironmentName));
        var config = SampleConfig();

        var path = await writer.WriteAsync(config);

        File.Exists(path + ".tmp").Should().BeFalse(
            "atomic write renames the .tmp into place; leftover .tmp = a partial write");
    }

    [Fact]
    public async Task WriteAsync_EmptyRunId_Throws()
    {
        var writer = new HeadlessConfigWriter(new FakeEnv(HostEnvironmentExtensions.EmbeddedEnvironmentName));
        var config = SampleConfig() with { RunId = Guid.Empty };

        var act = async () => await writer.WriteAsync(config);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*RunId*");
    }

    private static HeadlessRunConfig SampleConfig() => new()
    {
        SchemaVersion = 1,
        RunId = Guid.NewGuid(),
        Agent = new HeadlessAgent { Slug = "triage-agent", Instructions = "..." },
        Model = new HeadlessModel { Provider = "anthropic", Id = "claude-sonnet-4-6" },
        Workspace = new HeadlessWorkspace { Root = "/workspace" },
        Output = new HeadlessOutput { File = "/workspace/.andy-run/output.json", Stream = "stdout" },
        Limits = new HeadlessLimits { MaxIterations = 50, TimeoutSeconds = 300 },
    };

    private sealed class FakeEnv : IHostEnvironment
    {
        public FakeEnv(string name) { EnvironmentName = name; }
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
