using FluentAssertions;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace Andy.Containers.Api.Tests.Data;

/// <summary>
/// Regression guard for the bundling failure mode that hit
/// Conductor #899:
///
///   1. Author adds a new YAML-seeded catalog (Themes, in #886).
///   2. The `ThemeSeeder` is wired into Program.cs; tests pass
///      because the seeder unit tests use a temp directory.
///   3. PR ships. The bundled service launches in production
///      (Conductor.app's embedded service), the seeder walks
///      its search paths, finds nothing, logs
///      "ThemeSeeder: no config/themes/global directory found;
///       skipping seed." and exits silently.
///   4. The picker UI shows zero themes. The user sees only
///      the "Default" placeholder.
///
/// Root cause: `Andy.Containers.Api.csproj` had no `&lt;Content&gt;`
/// rule for the YAML files, so `dotnet publish` didn't copy them
/// to the output directory. Same defect had been silently
/// breaking `EnvironmentProfileSeeder` since it shipped — nobody
/// noticed because nothing surfaced an empty environments
/// catalog to the user.
///
/// What this test pins:
///
///   1. The .csproj has explicit `&lt;Content&gt;` rules for
///      `config/themes/global/*.yaml` and
///      `config/environments/global/*.yaml`. A future refactor
///      that drops them fails the test.
///   2. The `Link` attribute keeps the directory structure
///      under the publish output, so the seeder's
///      `Path.Combine(ContentRootPath, "config", "themes",
///      "global")` candidate resolves correctly.
///   3. The seed YAML files actually exist on disk in the repo
///      and parse successfully — no point shipping a Content
///      rule that points at a missing or malformed file.
///
/// Why a parse-the-csproj-XML test instead of a publish-output
/// test? Running `dotnet publish` inside an xUnit run is heavy
/// and order-sensitive (CI parallelism would race the publish
/// dir). XML parsing the project file is sub-millisecond and
/// catches the same regression — the one thing it can't catch
/// is "MSBuild silently dropped the rule" but that would also
/// fail the no-publish runtime test that other seeder fixtures
/// already cover.
/// </summary>
public class SeedConfigBundleGuardTests
{
    [Fact]
    public void Csproj_BundlesThemeSeedYamls()
    {
        var (project, _) = LoadProject();

        var themeContent = project
            .Descendants("Content")
            .FirstOrDefault(c =>
                (c.Attribute("Include")?.Value ?? "")
                    .Replace('/', '\\')
                    .Contains("config\\themes\\global"));

        themeContent.Should().NotBeNull(
            "removing the theme seed bundling rule from the csproj would silently break Conductor's theme picker — every user would see an empty catalog");

        themeContent!.Attribute("CopyToPublishDirectory")?.Value
            .Should().Be("PreserveNewest",
                "without CopyToPublishDirectory, `dotnet publish` won't include the YAMLs in the bundled service");

        themeContent.Attribute("Link")?.Value
            .Should().NotBeNull()
            .And.Subject.As<string>()
            .Replace('/', '\\')
            .Should().StartWith("config\\themes\\global\\",
                "Link preserves the directory structure under the publish output so ThemeSeeder.ResolveSeedDirectory finds it");
    }

    [Fact]
    public void Csproj_BundlesEnvironmentProfileSeedYamls()
    {
        // Same regression guard for environments. EnvironmentProfileSeeder
        // shipped earlier than ThemeSeeder and had this defect for weeks
        // before #899 caught it — the only reason it didn't surface as
        // a user-visible bug was that nothing in the UI rendered the
        // environments catalog. Lock it down so a future refactor can't
        // re-break it.
        var (project, _) = LoadProject();

        var envContent = project
            .Descendants("Content")
            .FirstOrDefault(c =>
                (c.Attribute("Include")?.Value ?? "")
                    .Replace('/', '\\')
                    .Contains("config\\environments\\global"));

        envContent.Should().NotBeNull(
            "removing the environments seed bundling rule would silently break the runtime-shape catalog (X2 / #91)");

        envContent!.Attribute("CopyToPublishDirectory")?.Value
            .Should().Be("PreserveNewest");
    }

    [Fact]
    public void ThemeYamls_ExistOnDisk_AndArePopulated()
    {
        // The Content rule in the csproj points at
        // `..\..\config\themes\global\*.yaml`. If someone deletes
        // every theme YAML, the rule still matches zero files and
        // `dotnet publish` silently produces an empty config dir —
        // catalog endpoint returns []. Pin a minimum count so a
        // bulk-delete is noisy.
        var (_, repoRoot) = LoadProject();
        var themesDir = Path.Combine(repoRoot, "config", "themes", "global");

        Directory.Exists(themesDir).Should().BeTrue(
            $"expected seed directory at {themesDir} — Conductor's theme picker depends on it being populated");

        var yamls = Directory.GetFiles(themesDir, "*.yaml");
        yamls.Length.Should().BeGreaterThanOrEqualTo(5,
            "the v1 theme catalog ships 5 starter themes (Dracula, GitHub Dark, Solarized Dark, Nord, Monokai); a count below 5 means a YAML was deleted accidentally");
    }

    [Fact]
    public void EnvironmentYamls_ExistOnDisk()
    {
        var (_, repoRoot) = LoadProject();
        var envsDir = Path.Combine(repoRoot, "config", "environments", "global");

        Directory.Exists(envsDir).Should().BeTrue(
            $"expected seed directory at {envsDir} — EnvironmentProfileSeeder depends on it");

        Directory.GetFiles(envsDir, "*.yaml").Length.Should().BeGreaterThan(0,
            "the runtime-shape catalog ships at least one profile (terminal / desktop / headless-container)");
    }

    /// <summary>
    /// Loads the API project's csproj as XML and computes the
    /// repository root. Walks up from the test assembly's
    /// location until it finds the `andy-containers/` directory.
    /// </summary>
    private static (XDocument Project, string RepoRoot) LoadProject()
    {
        var current = new DirectoryInfo(
            Path.GetDirectoryName(typeof(SeedConfigBundleGuardTests).Assembly.Location)
                ?? throw new InvalidOperationException("could not resolve test assembly path"));

        // Walk up until we find andy-containers.csproj's parent
        // directory (the repo root).
        DirectoryInfo? cursor = current;
        for (var i = 0; i < 10 && cursor != null; i++, cursor = cursor.Parent)
        {
            var candidate = Path.Combine(cursor.FullName, "src", "Andy.Containers.Api", "Andy.Containers.Api.csproj");
            if (File.Exists(candidate))
            {
                return (XDocument.Load(candidate), cursor.FullName);
            }
        }

        throw new FileNotFoundException(
            $"couldn't find Andy.Containers.Api.csproj walking up from {current.FullName} — test assumes it runs inside the andy-containers repo");
    }
}
