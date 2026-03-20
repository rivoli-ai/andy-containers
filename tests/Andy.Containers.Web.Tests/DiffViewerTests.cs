using Bunit;
using Andy.Containers.Web.Shared;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Web.Tests;

public class DiffViewerTests : TestContext
{
    [Fact]
    public void ShowsNoContentWhenBothEmpty()
    {
        var cut = RenderComponent<DiffViewer>(p => p
            .Add(s => s.Original, "")
            .Add(s => s.Modified, ""));

        cut.Find(".diff-empty").Should().NotBeNull();
        cut.Markup.Should().Contain("No content to compare.");
    }

    [Fact]
    public void ShowsNoChangesWhenIdentical()
    {
        var yaml = "kind: Template\nname: test";
        var cut = RenderComponent<DiffViewer>(p => p
            .Add(s => s.Original, yaml)
            .Add(s => s.Modified, yaml));

        cut.Find(".diff-no-changes").Should().NotBeNull();
        cut.Markup.Should().Contain("No changes");
    }

    [Fact]
    public void ShowsAddedLinesWhenContentAdded()
    {
        var original = "line1";
        var modified = "line1\nline2\nline3";

        var cut = RenderComponent<DiffViewer>(p => p
            .Add(s => s.Original, original)
            .Add(s => s.Modified, modified));

        var addedLines = cut.FindAll(".diff-added");
        addedLines.Should().NotBeEmpty();

        // Should contain the added text
        cut.Markup.Should().Contain("line2");
        cut.Markup.Should().Contain("line3");
    }

    [Fact]
    public void ShowsRemovedLinesWhenContentRemoved()
    {
        var original = "line1\nline2\nline3";
        var modified = "line1";

        var cut = RenderComponent<DiffViewer>(p => p
            .Add(s => s.Original, original)
            .Add(s => s.Modified, modified));

        var removedLines = cut.FindAll(".diff-removed");
        removedLines.Should().NotBeEmpty();
    }

    [Fact]
    public void ShowsMixedDiffWithAddedAndRemoved()
    {
        var original = "line1\nline2\nline3";
        var modified = "line1\nmodified-line\nline3";

        var cut = RenderComponent<DiffViewer>(p => p
            .Add(s => s.Original, original)
            .Add(s => s.Modified, modified));

        var removedLines = cut.FindAll(".diff-removed");
        var addedLines = cut.FindAll(".diff-added");
        var contextLines = cut.FindAll(".diff-context");

        removedLines.Should().NotBeEmpty("removed lines should be shown");
        addedLines.Should().NotBeEmpty("added lines should be shown");
        contextLines.Should().NotBeEmpty("unchanged context lines should be shown");
    }
}
