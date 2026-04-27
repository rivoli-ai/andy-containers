using Andy.Containers.Api.Data;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Data;

// X2 (rivoli-ai/andy-containers#91). The seeder loads YAML at startup,
// upserts by Name, and must never crash the host on a bad file. These
// tests pin those three invariants by pointing the seeder at a per-test
// temp directory we control byte-for-byte — no dependency on the real
// config/environments/global files at the repo root.
public class EnvironmentProfileSeederTests : IDisposable
{
    private readonly string _seedDir;
    private readonly Mock<IHostEnvironment> _env = new();

    public EnvironmentProfileSeederTests()
    {
        _seedDir = Path.Combine(
            Path.GetTempPath(),
            $"env-seeder-{Guid.NewGuid():N}",
            "config", "environments", "global");
        Directory.CreateDirectory(_seedDir);

        // The seeder walks up looking for config/environments/global; we
        // give it a content root one level deeper than the directory we
        // actually populated so its first candidate (../../config/...)
        // hits our temp path.
        var contentRoot = Path.GetFullPath(Path.Combine(_seedDir, "..", "..", "..", "src", "Andy.Containers.Api"));
        Directory.CreateDirectory(contentRoot);
        _env.Setup(e => e.ContentRootPath).Returns(contentRoot);
    }

    public void Dispose()
    {
        // Walk up to the per-test root and delete the whole tree.
        var root = Directory.GetParent(Directory.GetParent(_seedDir)!.FullName)!.FullName;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task SeedAsync_FreshDb_InsertsAllProfilesWithExpectedCapabilities()
    {
        WriteSeed("headless-container.yaml", HeadlessContainerYaml);
        WriteSeed("terminal.yaml", TerminalYaml);
        WriteSeed("desktop.yaml", DesktopYaml);

        using var db = InMemoryDbHelper.CreateContext();
        var changes = await EnvironmentProfileSeeder.SeedAsync(db, _env.Object, NullLogger.Instance);

        changes.Should().Be(3);
        var profiles = await db.EnvironmentProfiles.OrderBy(p => p.Name).ToListAsync();
        profiles.Should().HaveCount(3);
        profiles.Select(p => p.Name).Should().BeEquivalentTo(new[]
        {
            "desktop", "headless-container", "terminal",
        });

        var headless = profiles.Single(p => p.Name == "headless-container");
        headless.Kind.Should().Be(EnvironmentKind.HeadlessContainer);
        headless.BaseImageRef.Should().Be("ghcr.io/rivoli-ai/andy-headless:latest");
        headless.Capabilities.HasGui.Should().BeFalse();
        headless.Capabilities.SecretsScope.Should().Be(SecretsScope.WorkspaceScoped);
        headless.Capabilities.AuditMode.Should().Be(AuditMode.Strict);
        headless.Capabilities.NetworkAllowlist.Should().Contain("registry.rivoli.ai");

        var desktop = profiles.Single(p => p.Name == "desktop");
        desktop.Capabilities.HasGui.Should().BeTrue();
        desktop.Capabilities.SecretsScope.Should().Be(SecretsScope.OrganizationScoped);
        desktop.Capabilities.NetworkAllowlist.Should().BeEquivalentTo(new[] { "*" });
    }

    [Fact]
    public async Task SeedAsync_RerunOnSeededDb_IsIdempotent_NoDuplicates()
    {
        WriteSeed("headless-container.yaml", HeadlessContainerYaml);

        using var db = InMemoryDbHelper.CreateContext();
        var first = await EnvironmentProfileSeeder.SeedAsync(db, _env.Object, NullLogger.Instance);
        var second = await EnvironmentProfileSeeder.SeedAsync(db, _env.Object, NullLogger.Instance);

        first.Should().Be(1);
        second.Should().Be(0, "the second pass must observe the existing row and skip");
        (await db.EnvironmentProfiles.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SeedAsync_RerunPreservesOperatorEdits()
    {
        // Operator (X3 catalog API) flipped audit_mode to None on a seeded
        // row. A subsequent restart must not stomp that change.
        WriteSeed("headless-container.yaml", HeadlessContainerYaml);
        using var db = InMemoryDbHelper.CreateContext();
        await EnvironmentProfileSeeder.SeedAsync(db, _env.Object, NullLogger.Instance);

        var row = await db.EnvironmentProfiles.SingleAsync();
        row.Capabilities.AuditMode = AuditMode.None;
        row.DisplayName = "Operator-renamed";
        await db.SaveChangesAsync();

        await EnvironmentProfileSeeder.SeedAsync(db, _env.Object, NullLogger.Instance);

        var reloaded = await db.EnvironmentProfiles.SingleAsync();
        reloaded.Capabilities.AuditMode.Should().Be(AuditMode.None);
        reloaded.DisplayName.Should().Be("Operator-renamed");
    }

    [Fact]
    public async Task SeedAsync_MalformedYaml_LogsAndSkips_DoesNotThrow()
    {
        WriteSeed("good.yaml", HeadlessContainerYaml);
        WriteSeed("broken.yaml", "code: oops\n  bad: indent:: ::\n");

        using var db = InMemoryDbHelper.CreateContext();
        var changes = await EnvironmentProfileSeeder.SeedAsync(db, _env.Object, NullLogger.Instance);

        changes.Should().Be(1, "only the well-formed file should land");
        (await db.EnvironmentProfiles.SingleAsync()).Name.Should().Be("headless-container");
    }

    [Fact]
    public async Task SeedAsync_MissingRequiredFields_LogsAndSkips()
    {
        WriteSeed("incomplete.yaml", "code: only-a-code\n");

        using var db = InMemoryDbHelper.CreateContext();
        var changes = await EnvironmentProfileSeeder.SeedAsync(db, _env.Object, NullLogger.Instance);

        changes.Should().Be(0);
        (await db.EnvironmentProfiles.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SeedAsync_UnknownKind_LogsAndSkips()
    {
        WriteSeed("alien.yaml",
            "code: alien\n" +
            "display_name: Alien\n" +
            "kind: AlienShape\n" +
            "base_image_ref: ghcr.io/x:y\n");

        using var db = InMemoryDbHelper.CreateContext();
        var changes = await EnvironmentProfileSeeder.SeedAsync(db, _env.Object, NullLogger.Instance);

        changes.Should().Be(0);
    }

    [Fact]
    public async Task SeedAsync_NoSeedDirectory_ReturnsZeroWithoutThrowing()
    {
        // Wipe the directory entirely so the resolver finds nothing.
        Directory.Delete(_seedDir, recursive: true);

        using var db = InMemoryDbHelper.CreateContext();
        var changes = await EnvironmentProfileSeeder.SeedAsync(db, _env.Object, NullLogger.Instance);

        changes.Should().Be(0);
    }

    private void WriteSeed(string fileName, string yaml)
    {
        File.WriteAllText(Path.Combine(_seedDir, fileName), yaml);
    }

    private const string HeadlessContainerYaml = """
        code: headless-container
        display_name: Headless container
        kind: HeadlessContainer
        base_image_ref: ghcr.io/rivoli-ai/andy-headless:latest
        capabilities:
          network_allowlist:
            - registry.rivoli.ai
            - api.github.com
            - pypi.org
            - nuget.org
          secrets_scope: WorkspaceScoped
          has_gui: false
          audit_mode: Strict
        """;

    private const string TerminalYaml = """
        code: terminal
        display_name: Terminal session
        kind: Terminal
        base_image_ref: ghcr.io/rivoli-ai/andy-terminal:latest
        capabilities:
          network_allowlist:
            - "*"
          secrets_scope: WorkspaceScoped
          has_gui: false
          audit_mode: Standard
        """;

    private const string DesktopYaml = """
        code: desktop
        display_name: Desktop session
        kind: Desktop
        base_image_ref: ghcr.io/rivoli-ai/andy-desktop:latest
        capabilities:
          network_allowlist:
            - "*"
          secrets_scope: OrganizationScoped
          has_gui: true
          audit_mode: Standard
        """;
}
