using Andy.Containers.Api.Data;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Data;

// X9 (rivoli-ai/andy-containers#99). Round-trip the actual production
// YAML files (config/environments/global/*.yaml) through the real
// seeder. The X2 tests use synthetic strings; nothing yet pins that
// the files we ship parse cleanly. This class catches drift if
// someone edits a profile file with an unparseable enum value or a
// typo in the schema.
public class EnvironmentProfileSeederProductionYamlTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IHostEnvironment> _env = new();

    public EnvironmentProfileSeederProductionYamlTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        // Walk up from the test binary
        // (tests/Andy.Containers.Api.Tests/bin/Debug/net8.0) until we
        // find the worktree root that contains config/environments.
        // The seeder's `ContentRootPath/config/environments/global`
        // candidate hits when ContentRootPath is the worktree root.
        // Bounded walk so a misconfigured CI checkout doesn't loop.
        _env.Setup(e => e.ContentRootPath).Returns(FindWorktreeRoot());
    }

    private static string FindWorktreeRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "config", "environments", "global")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException(
            $"Could not find config/environments/global walking up from {AppContext.BaseDirectory}");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void ProductionSeedDirectory_ResolvesFromTestBinary()
    {
        var dir = EnvironmentProfileSeeder.ResolveSeedDirectory(_env.Object);
        dir.Should().NotBeNull(
            "the seeder's path-walk must find config/environments/global from the test binary; " +
            "if this fails, the search-paths logic drifted from the actual repo layout");
        Directory.GetFiles(dir!, "*.yaml").Should().HaveCount(3,
            "headless-container.yaml + terminal.yaml + desktop.yaml are the three seeded files");
    }

    [Fact]
    public async Task SeedAsync_AgainstProductionFiles_LoadsThreeProfiles()
    {
        var changes = await EnvironmentProfileSeeder.SeedAsync(_db, _env.Object, NullLogger.Instance);

        changes.Should().Be(3, "the three production YAML files must all parse cleanly");
        var names = await _db.EnvironmentProfiles
            .OrderBy(p => p.Name)
            .Select(p => p.Name)
            .ToListAsync();
        names.Should().BeEquivalentTo(new[] { "desktop", "headless-container", "terminal" });
    }

    [Fact]
    public async Task SeedAsync_AgainstProductionFiles_HeadlessHasStrictAuditAndRestrictedEgress()
    {
        await EnvironmentProfileSeeder.SeedAsync(_db, _env.Object, NullLogger.Instance);

        var headless = await _db.EnvironmentProfiles.SingleAsync(p => p.Name == "headless-container");
        headless.Kind.Should().Be(EnvironmentKind.HeadlessContainer);
        headless.Capabilities.AuditMode.Should().Be(AuditMode.Strict,
            "headless agents run unattended; full audit is the contract");
        headless.Capabilities.HasGui.Should().BeFalse();
        headless.Capabilities.SecretsScope.Should().Be(SecretsScope.WorkspaceScoped);
        // The seeded file must restrict egress to platform services + canonical
        // package mirrors. A wildcard would defeat the unattended-agent contract.
        headless.Capabilities.NetworkAllowlist.Should().NotContain("*");
        headless.Capabilities.NetworkAllowlist.Should().Contain("registry.rivoli.ai");
    }

    [Fact]
    public async Task SeedAsync_AgainstProductionFiles_DesktopFlipsGuiOn()
    {
        await EnvironmentProfileSeeder.SeedAsync(_db, _env.Object, NullLogger.Instance);

        var desktop = await _db.EnvironmentProfiles.SingleAsync(p => p.Name == "desktop");
        desktop.Kind.Should().Be(EnvironmentKind.Desktop);
        desktop.Capabilities.HasGui.Should().BeTrue(
            "desktop is the only profile that ships a GUI; X4 keys VNC sidecar provisioning on this");
    }
}
