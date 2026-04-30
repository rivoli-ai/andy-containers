using Andy.Containers.Api.Data;
using Andy.Containers.Api.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using System.Text.Json;

namespace Andy.Containers.Api.Tests.Data;

// Conductor #886. The seeder loads YAML at startup, upserts by
// Theme.Id, and must never crash the host on a bad file. These
// tests pin the contract: fresh insert / idempotent re-run /
// upsert on YAML edits / invalid file skipped without aborting.
public class ThemeSeederTests : IDisposable
{
    private readonly string _seedDir;
    private readonly Mock<IHostEnvironment> _env = new();

    public ThemeSeederTests()
    {
        _seedDir = Path.Combine(
            Path.GetTempPath(),
            $"theme-seeder-{Guid.NewGuid():N}",
            "config", "themes", "global");
        Directory.CreateDirectory(_seedDir);

        var contentRoot = Path.GetFullPath(Path.Combine(_seedDir, "..", "..", "..", "src", "Andy.Containers.Api"));
        Directory.CreateDirectory(contentRoot);
        _env.Setup(e => e.ContentRootPath).Returns(contentRoot);
    }

    public void Dispose()
    {
        var root = Directory.GetParent(Directory.GetParent(_seedDir)!.FullName)!.FullName;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task SeedAsync_FreshDb_InsertsThemesWithParsedPalette()
    {
        WriteSeed("dracula.yaml", DraculaYaml);
        WriteSeed("nord.yaml", NordYaml);

        using var db = InMemoryDbHelper.CreateContext();
        var changes = await ThemeSeeder.SeedAsync(db, _env.Object, NullLogger.Instance);

        changes.Should().Be(2);
        var themes = await db.Themes.OrderBy(t => t.Id).ToListAsync();
        themes.Should().HaveCount(2);
        themes.Select(t => t.Id).Should().BeEquivalentTo(new[] { "dracula", "nord" });

        var dracula = themes.Single(t => t.Id == "dracula");
        dracula.DisplayName.Should().Be("Dracula");
        dracula.Kind.Should().Be("terminal");
        dracula.Version.Should().Be(1);

        // Palette must round-trip cleanly through the JSON
        // envelope — a malformed serialise would silently ship
        // an empty palette downstream and break every theme that
        // depends on this seeder.
        var palette = JsonSerializer.Deserialize<Dictionary<string, string>>(dracula.PaletteJson)!;
        palette["background"].Should().Be("#282a36");
        palette["foreground"].Should().Be("#f8f8f2");
        palette["ansi_4"].Should().Be("#bd93f9");
    }

    [Fact]
    public async Task SeedAsync_RerunOnUnchangedYaml_ReportsZeroChanges()
    {
        // Idempotency: re-running the seeder with the same YAML
        // must not produce phantom write traffic. A `changes > 0`
        // here would mean every host start dirties the row even
        // when nothing changed, which trips downstream
        // notifications + breaks the "no-op startup" promise.
        WriteSeed("dracula.yaml", DraculaYaml);

        using var db = InMemoryDbHelper.CreateContext();
        await ThemeSeeder.SeedAsync(db, _env.Object, NullLogger.Instance);

        var second = await ThemeSeeder.SeedAsync(db, _env.Object, NullLogger.Instance);
        second.Should().Be(0, "re-running on an unchanged YAML must produce zero updates");
    }

    [Fact]
    public async Task SeedAsync_RerunAfterYamlEdit_RefreshesRowFields()
    {
        // The contract for theme seed (unlike EnvironmentProfile):
        // YAML is the source of truth. An operator-edited YAML
        // must reconcile to the DB on next start. This is what
        // distinguishes our upsert from EnvironmentProfileSeeder's
        // insert-only path.
        WriteSeed("dracula.yaml", DraculaYaml);

        using var db = InMemoryDbHelper.CreateContext();
        await ThemeSeeder.SeedAsync(db, _env.Object, NullLogger.Instance);

        // Edit the YAML — bump display name + version.
        WriteSeed("dracula.yaml", DraculaYaml.Replace("Dracula", "Dracula Pro").Replace("version: 1", "version: 2"));
        var changes = await ThemeSeeder.SeedAsync(db, _env.Object, NullLogger.Instance);

        changes.Should().Be(1);
        var dracula = await db.Themes.SingleAsync(t => t.Id == "dracula");
        dracula.DisplayName.Should().Be("Dracula Pro");
        dracula.Version.Should().Be(2);
    }

    [Fact]
    public async Task SeedAsync_MalformedYaml_SkipsWithoutAborting()
    {
        // Host startup must NEVER abort on a bad seed entry —
        // operators iterate on YAML files locally and a missing
        // colon shouldn't take the service offline. The other
        // (well-formed) files in the directory still seed.
        WriteSeed("dracula.yaml", DraculaYaml);
        WriteSeed("broken.yaml", "this: : invalid: yaml");

        using var db = InMemoryDbHelper.CreateContext();
        var changes = await ThemeSeeder.SeedAsync(db, _env.Object, NullLogger.Instance);

        // Dracula seeded; the malformed file produced no row.
        changes.Should().Be(1);
        var themes = await db.Themes.ToListAsync();
        themes.Should().HaveCount(1);
        themes.Single().Id.Should().Be("dracula");
    }

    [Fact]
    public async Task SeedAsync_FileMissingRequiredFields_SkipsWithWarning()
    {
        // No id, no name, no display_name → skip. The seed
        // path has to be defensive against operator typos.
        WriteSeed("missing-fields.yaml", "kind: terminal\nversion: 1\npalette:\n  background: '#000'");

        using var db = InMemoryDbHelper.CreateContext();
        var changes = await ThemeSeeder.SeedAsync(db, _env.Object, NullLogger.Instance);

        changes.Should().Be(0);
        var themes = await db.Themes.ToListAsync();
        themes.Should().BeEmpty();
    }

    [Fact]
    public async Task SeedAsync_FileWithEmptyPalette_Skipped()
    {
        // A theme with no palette is useless to consumers — we
        // skip it so downstream code never has to handle the
        // "valid theme, no colours" edge case.
        WriteSeed("empty.yaml",
            "id: empty\nname: empty\ndisplay_name: Empty\nkind: terminal\nversion: 1\npalette: {}\n");

        using var db = InMemoryDbHelper.CreateContext();
        var changes = await ThemeSeeder.SeedAsync(db, _env.Object, NullLogger.Instance);

        changes.Should().Be(0);
    }

    // MARK: - Helpers

    private void WriteSeed(string filename, string contents)
    {
        File.WriteAllText(Path.Combine(_seedDir, filename), contents);
    }

    private const string DraculaYaml = @"id: dracula
name: dracula
display_name: Dracula
kind: terminal
version: 1
palette:
  background: ""#282a36""
  foreground: ""#f8f8f2""
  cursor: ""#f8f8f2""
  selection: ""#44475a""
  ansi_0: ""#21222c""
  ansi_1: ""#ff5555""
  ansi_2: ""#50fa7b""
  ansi_3: ""#f1fa8c""
  ansi_4: ""#bd93f9""
  ansi_5: ""#ff79c6""
  ansi_6: ""#8be9fd""
  ansi_7: ""#f8f8f2""
";

    private const string NordYaml = @"id: nord
name: nord
display_name: Nord
kind: terminal
version: 1
palette:
  background: ""#2e3440""
  foreground: ""#d8dee9""
";
}
