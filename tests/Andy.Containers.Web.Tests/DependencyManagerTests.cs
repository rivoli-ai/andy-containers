using Andy.Containers.Web.Shared;
using Bunit;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Web.Tests;

public class DependencyManagerTests : TestContext
{
    private static List<DependencyItem> CreateTestDependencies() =>
    [
        new DependencyItem { Type = "sdk", Name = "dotnet-sdk", VersionConstraint = "8.0.*", Status = DependencyStatus.Resolved, AutoUpdate = true, UpdatePolicy = "patch" },
        new DependencyItem { Type = "runtime", Name = "python", VersionConstraint = "3.12.*", Status = DependencyStatus.UpdateAvailable },
        new DependencyItem { Type = "tool", Name = "git", VersionConstraint = "latest", Status = DependencyStatus.NotResolved }
    ];

    [Fact]
    public void RendersEmptyStateWhenNoDependencies()
    {
        var cut = RenderComponent<DependencyManager>(p => p
            .Add(s => s.Dependencies, new List<DependencyItem>()));

        cut.Markup.Should().Contain("empty-state");
        cut.Markup.Should().Contain("No dependencies declared");
        cut.Markup.Should().Contain("0 dependencies");
    }

    [Fact]
    public void RendersDependencyTableWithItems()
    {
        var deps = CreateTestDependencies();
        var cut = RenderComponent<DependencyManager>(p => p
            .Add(s => s.Dependencies, deps));

        cut.Markup.Should().Contain("data-table");
        cut.Markup.Should().Contain("dotnet-sdk");
        cut.Markup.Should().Contain("python");
        cut.Markup.Should().Contain("git");
        cut.Markup.Should().Contain("3 dependencies");
    }

    [Fact]
    public void ShowsTypeIconForEachDependencyType()
    {
        DependencyManager.GetTypeIcon("sdk").Should().Be("bi bi-box-seam");
        DependencyManager.GetTypeIcon("runtime").Should().Be("bi bi-play-circle");
        DependencyManager.GetTypeIcon("compiler").Should().Be("bi bi-gear");
        DependencyManager.GetTypeIcon("tool").Should().Be("bi bi-wrench");
        DependencyManager.GetTypeIcon("library").Should().Be("bi bi-book");
        DependencyManager.GetTypeIcon("other").Should().Be("bi bi-box");
    }

    [Fact]
    public void ShowsStatusBadgeForEachStatus()
    {
        DependencyManager.GetStatusLabel(DependencyStatus.NotResolved).Should().Be("Not resolved");
        DependencyManager.GetStatusLabel(DependencyStatus.Resolved).Should().Be("Resolved");
        DependencyManager.GetStatusLabel(DependencyStatus.UpdateAvailable).Should().Be("Update available");
        DependencyManager.GetStatusLabel(DependencyStatus.ConstraintViolation).Should().Be("Constraint violation");

        DependencyManager.GetStatusClass(DependencyStatus.Resolved).Should().Be("running");
        DependencyManager.GetStatusClass(DependencyStatus.UpdateAvailable).Should().Be("pending");
        DependencyManager.GetStatusClass(DependencyStatus.ConstraintViolation).Should().Be("failed");
        DependencyManager.GetStatusClass(DependencyStatus.NotResolved).Should().Be("unknown");
    }

    [Fact]
    public void AddButtonOpensForm()
    {
        var cut = RenderComponent<DependencyManager>(p => p
            .Add(s => s.Dependencies, new List<DependencyItem>()));

        cut.Markup.Should().NotContain("Add Dependency</h5>");

        var addBtn = cut.Find("button.btn.btn-primary.btn-sm");
        addBtn.Click();

        cut.Markup.Should().Contain("Add Dependency</h5>");
        cut.Markup.Should().Contain("form-control");
    }

    [Fact]
    public void CancelButtonClosesForm()
    {
        var cut = RenderComponent<DependencyManager>(p => p
            .Add(s => s.Dependencies, new List<DependencyItem>()));

        // Open form
        cut.Find("button.btn.btn-primary.btn-sm").Click();
        cut.Markup.Should().Contain("Add Dependency</h5>");

        // Cancel
        var cancelBtn = cut.FindAll("button.btn.btn-secondary.btn-sm")
            .First(b => b.TextContent.Contains("Cancel"));
        cancelBtn.Click();

        cut.Markup.Should().NotContain("Add Dependency</h5>");
    }

    [Fact]
    public void ToggleToCardViewShowsCards()
    {
        var deps = CreateTestDependencies();
        var cut = RenderComponent<DependencyManager>(p => p
            .Add(s => s.Dependencies, deps));

        // Initially in table view
        cut.Markup.Should().Contain("data-table");

        // Click card view toggle
        var cardBtn = cut.FindAll("button[title='Card view']");
        cardBtn.Should().HaveCount(1);
        cardBtn[0].Click();

        cut.Markup.Should().Contain("card-grid");
        cut.Markup.Should().Contain("dotnet-sdk");
    }

    [Fact]
    public void RemoveButtonShowsConfirmation()
    {
        var deps = CreateTestDependencies();
        var cut = RenderComponent<DependencyManager>(p => p
            .Add(s => s.Dependencies, deps));

        // Click remove on first item
        var removeBtn = cut.FindAll("button[title='Remove']")[0];
        removeBtn.Click();

        cut.Markup.Should().Contain("Remove dependency");
        cut.Markup.Should().Contain("dotnet-sdk");
    }

    [Fact]
    public void EditButtonOpensFormWithPreFilledData()
    {
        var deps = CreateTestDependencies();
        var cut = RenderComponent<DependencyManager>(p => p
            .Add(s => s.Dependencies, deps));

        // Click edit on first item
        var editBtn = cut.FindAll("button[title='Edit']")[0];
        editBtn.Click();

        cut.Markup.Should().Contain("Edit Dependency</h5>");

        // Check form has pre-filled data
        var nameInput = cut.Find("input.form-control[placeholder='e.g. dotnet-sdk']");
        nameInput.GetAttribute("value").Should().Be("dotnet-sdk");
    }

    [Fact]
    public void ShowsAutoUpdateInfoWhenEnabled()
    {
        var deps = CreateTestDependencies();
        var cut = RenderComponent<DependencyManager>(p => p
            .Add(s => s.Dependencies, deps));

        // dotnet-sdk has AutoUpdate=true with policy=patch
        cut.Markup.Should().Contain("bi-check-circle-fill");
        cut.Markup.Should().Contain("patch");

        // git has AutoUpdate=false
        // Check that "Off" appears for non-auto-update items
        cut.Markup.Should().Contain("Off");
    }

    [Fact]
    public void StatusBadgesRenderedInTable()
    {
        var deps = CreateTestDependencies();
        var cut = RenderComponent<DependencyManager>(p => p
            .Add(s => s.Dependencies, deps));

        cut.Markup.Should().Contain("Resolved");
        cut.Markup.Should().Contain("Update available");
        cut.Markup.Should().Contain("Not resolved");
    }
}
