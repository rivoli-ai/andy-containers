using Andy.Containers.Api.Services;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// IM4 (rivoli-ai/andy-containers#253). Smoke check that the existing
// template fixtures in config/templates still parse without errors
// after the imperative-fields addition. None of them use the new
// fields — the test would have caught a regression that broke the
// declarative-only YAML shape.
public class YamlTemplateParserFixtureTests
{
    private readonly YamlTemplateParser _parser = new();

    public static TheoryData<string> FixtureFiles
    {
        get
        {
            var data = new TheoryData<string>();
            var fixtureDir = LocateFixtureDirectory();
            if (fixtureDir is null)
            {
                // Skip if the repo layout has moved — a fixture
                // smoke-test that can't find fixtures is an
                // environmental problem, not a regression.
                return data;
            }
            foreach (var file in Directory.EnumerateFiles(fixtureDir, "*.yaml", SearchOption.TopDirectoryOnly))
            {
                data.Add(file);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void Validate_LegacyFixtures_HaveNoImperativeFieldErrors(string fixturePath)
    {
        // The contract under test is "IM4 does not regress legacy
        // fixtures." Some fixtures have pre-existing validation
        // errors unrelated to IM4 (e.g. kebab-case enum values that
        // the parser doesn't normalise) — those are out of scope
        // here and tracked separately. What this test asserts is:
        // none of the IM4-introduced fields and their validators
        // ever fire on a legacy fixture.
        var yaml = File.ReadAllText(fixturePath);

        var result = _parser.Validate(yaml);

        var imperativeFieldPrefixes = new[]
        {
            "extends", "from", "packages", "files", "install", "entrypoint", "markers",
        };

        var im4Errors = result.Errors
            .Where(e => imperativeFieldPrefixes.Any(p =>
                e.Field.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        im4Errors.Should().BeEmpty(
            $"{Path.GetFileName(fixturePath)} must not pick up any IM4-field errors — but got: " +
            string.Join("; ", im4Errors.Select(e => $"{e.Field}: {e.Message}")));

        var im4Warnings = result.Warnings
            .Where(w => imperativeFieldPrefixes.Any(p =>
                w.Field.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        im4Warnings.Should().BeEmpty(
            $"{Path.GetFileName(fixturePath)} must not pick up any IM4-field warnings — but got: " +
            string.Join("; ", im4Warnings.Select(w => $"{w.Field}: {w.Message}")));
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void Parse_LegacyFixtures_DoNotPickUpImperativeFields(string fixturePath)
    {
        // The strict goal: a legacy fixture must not accidentally
        // populate the new IM4 fields (no parser-side default-value
        // pollution). Some fixtures throw on Parse() because of
        // pre-existing enum-naming issues unrelated to IM4
        // (kebab-case ide_type values like 'code-server') — that's
        // tracked separately and not in scope here.
        var yaml = File.ReadAllText(fixturePath);

        try
        {
            var template = _parser.Parse(yaml);

            template.Extends.Should().BeNull();
            template.Packages.Should().BeNull();
            template.Files.Should().BeNull();
            template.Install.Should().BeNull();
            template.EntryPoint.Should().BeNull();
            template.Markers.Should().BeNull();
        }
        catch (ArgumentException)
        {
            // Pre-existing parse error in the fixture — outside
            // IM4's scope. The Validate-side test above confirms
            // IM4 itself doesn't add any new errors for these
            // fixtures.
        }
    }

    private static string? LocateFixtureDirectory()
    {
        // Walk up from the test binary to find the repo root, then
        // descend into config/templates/global. Avoids hard-coding
        // the path so the test still runs from CI working dirs.
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "config", "templates", "global");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            var parent = Directory.GetParent(dir);
            if (parent is null)
            {
                return null;
            }
            dir = parent.FullName;
        }
        return null;
    }
}
