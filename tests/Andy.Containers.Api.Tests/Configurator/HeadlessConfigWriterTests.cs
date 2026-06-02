using System.Text.Json;
using Andy.Containers.Api.Services;
using Andy.Containers.Configurator;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Andy.Containers.Api.Tests.Configurator;

// AP3 (rivoli-ai/andy-containers#105). Verifies the writer atomically
// produces a snake_case JSON file under a config-driven runs-root.
//
// The path is now selected by explicit config (the `Containers:HeadlessRunsRoot`
// setting / `ANDY_HEADLESS_RUNS_ROOT` env var), NOT by the retired "Embedded"
// hosting environment (conductor: "remove embedded across the board" — the
// daemon runs services as host processes, so the root-owned /var/run/andy
// default broke the host daemon with an UnauthorizedAccessException). When
// unset the writer defaults to a user-writable temp root.
public class HeadlessConfigWriterTests : IDisposable
{
    private readonly string _tempBase;
    private readonly string _customRoot;

    public HeadlessConfigWriterTests()
    {
        _tempBase = Path.Combine(Path.GetTempPath(), "andy-containers", "runs");
        _customRoot = Path.Combine(Path.GetTempPath(), "andy-containers-test-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        // Best-effort cleanup of run subdirs created during this run;
        // intentionally narrow so we never blow away unrelated state.
        CleanupRunDirs(_tempBase);
        if (Directory.Exists(_customRoot))
        {
            try { Directory.Delete(_customRoot, recursive: true); } catch { /* ignore */ }
        }
    }

    private static void CleanupRunDirs(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (Guid.TryParse(Path.GetFileName(dir), out _))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    // Empty configuration → no env var, no setting → user-writable temp default.
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    private static IConfiguration ConfigWithRoot(string root) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Containers:HeadlessRunsRoot"] = root,
            })
            .Build();

    [Fact]
    public async Task WriteAsync_DefaultsToUserWritableTempRoot()
    {
        var writer = new HeadlessConfigWriter(EmptyConfig());
        var config = SampleConfig();

        var path = await writer.WriteAsync(config);

        path.Should().StartWith(_tempBase,
            "with no configured runs-root the writer must default to the user-writable temp dir, " +
            "never the root-owned /var/run/andy that breaks the host daemon");
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
    public async Task WriteAsync_HonorsConfiguredRunsRoot()
    {
        var writer = new HeadlessConfigWriter(ConfigWithRoot(_customRoot));
        var config = SampleConfig();

        var path = await writer.WriteAsync(config);

        path.Should().StartWith(_customRoot,
            "an explicit Containers:HeadlessRunsRoot must be honored so hosted/Docker deployments " +
            "can still target /var/run/andy/runs");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_OverwritesExistingFile()
    {
        var writer = new HeadlessConfigWriter(EmptyConfig());
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
        var writer = new HeadlessConfigWriter(EmptyConfig());
        var config = SampleConfig();

        var path = await writer.WriteAsync(config);

        File.Exists(path + ".tmp").Should().BeFalse(
            "atomic write renames the .tmp into place; leftover .tmp = a partial write");
    }

    [Fact]
    public async Task WriteAsync_EmptyRunId_Throws()
    {
        var writer = new HeadlessConfigWriter(EmptyConfig());
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
}
