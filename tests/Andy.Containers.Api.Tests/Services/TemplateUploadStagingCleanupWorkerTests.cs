using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// #277 PR C. The sweeper's interesting unit is the per-tick
// decision (keep / delete) — the PeriodicTimer loop adds nothing
// testable. Tests call SweepOnceAsync directly, point the worker at
// a scratch staging root, and drive time via a fake clock so we
// don't have to `sleep` for the retention window.
public sealed class TemplateUploadStagingCleanupWorkerTests : IDisposable
{
    private readonly string _stagingRoot;
    private readonly FakeTime _clock = new(DateTimeOffset.Parse("2026-05-12T00:00:00Z"));

    public TemplateUploadStagingCleanupWorkerTests()
    {
        _stagingRoot = Directory.CreateTempSubdirectory("p1f4-partc-staging-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_stagingRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task SweepOnceAsync_DeletesUnreferencedDirOlderThanRetention()
    {
        var orphan = CreateStagingDir("orphan");
        AgeTo(orphan, _clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(8));

        using var db = InMemoryDbHelper.CreateContext();
        var worker = MakeWorker(db, retention: TimeSpan.FromDays(7));

        var deleted = await worker.SweepOnceAsync(CancellationToken.None);

        deleted.Should().Be(1);
        Directory.Exists(orphan).Should().BeFalse();
    }

    [Fact]
    public async Task SweepOnceAsync_KeepsDirReferencedByTemplate_EvenWhenOlderThanRetention()
    {
        var referenced = CreateStagingDir("referenced");
        AgeTo(referenced, _clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(30));

        using var db = InMemoryDbHelper.CreateContext();
        db.Templates.Add(new ContainerTemplate
        {
            Code = "t1",
            Name = "T1",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            UploadedFilesPath = referenced,
        });
        await db.SaveChangesAsync();

        var worker = MakeWorker(db, retention: TimeSpan.FromDays(7));

        var deleted = await worker.SweepOnceAsync(CancellationToken.None);

        deleted.Should().Be(0);
        Directory.Exists(referenced).Should().BeTrue(
            "force-rebuild of a long-lived template needs its uploaded files; referenced dirs are never reclaimed regardless of age.");
    }

    [Fact]
    public async Task SweepOnceAsync_KeepsDirYoungerThanRetention_EvenWhenUnreferenced()
    {
        var fresh = CreateStagingDir("fresh");
        AgeTo(fresh, _clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(1));

        using var db = InMemoryDbHelper.CreateContext();
        var worker = MakeWorker(db, retention: TimeSpan.FromDays(7));

        var deleted = await worker.SweepOnceAsync(CancellationToken.None);

        deleted.Should().Be(0);
        Directory.Exists(fresh).Should().BeTrue(
            "a multipart POST that crashed seconds ago might still be in the controller's `catch` cleanup — don't race it.");
    }

    [Fact]
    public async Task SweepOnceAsync_ReturnsZero_WhenStagingRootMissing()
    {
        Directory.Delete(_stagingRoot, recursive: true);

        using var db = InMemoryDbHelper.CreateContext();
        var worker = MakeWorker(db, retention: TimeSpan.FromDays(7));

        var deleted = await worker.SweepOnceAsync(CancellationToken.None);

        deleted.Should().Be(0);
    }

    [Fact]
    public async Task SweepOnceAsync_DeletesOnlyOrphans_WhenMixed()
    {
        var orphan1 = CreateStagingDir("orphan1");
        var orphan2 = CreateStagingDir("orphan2");
        var referenced = CreateStagingDir("referenced");
        var fresh = CreateStagingDir("fresh");

        var old = _clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(30);
        AgeTo(orphan1, old);
        AgeTo(orphan2, old);
        AgeTo(referenced, old);
        AgeTo(fresh, _clock.GetUtcNow().UtcDateTime - TimeSpan.FromHours(1));

        using var db = InMemoryDbHelper.CreateContext();
        db.Templates.Add(new ContainerTemplate
        {
            Code = "t1",
            Name = "T1",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            UploadedFilesPath = referenced,
        });
        await db.SaveChangesAsync();

        var worker = MakeWorker(db, retention: TimeSpan.FromDays(7));

        var deleted = await worker.SweepOnceAsync(CancellationToken.None);

        deleted.Should().Be(2);
        Directory.Exists(orphan1).Should().BeFalse();
        Directory.Exists(orphan2).Should().BeFalse();
        Directory.Exists(referenced).Should().BeTrue();
        Directory.Exists(fresh).Should().BeTrue();
    }

    private string CreateStagingDir(string name)
    {
        var dir = Path.Combine(_stagingRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "marker.txt"), name);
        return dir;
    }

    private static void AgeTo(string dir, DateTime utcTimestamp)
        => Directory.SetLastWriteTimeUtc(dir, utcTimestamp);

    private TemplateUploadStagingCleanupWorker MakeWorker(
        Andy.Containers.Infrastructure.Data.ContainersDbContext db,
        TimeSpan retention)
    {
        var options = new TemplateUploadStagingCleanupOptions { Retention = retention };
        return new TemplateUploadStagingCleanupWorker(
            InMemoryDbHelper.CreateScopeFactory(db),
            options,
            NullLogger<TemplateUploadStagingCleanupWorker>.Instance,
            _stagingRoot,
            _clock);
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTime(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
