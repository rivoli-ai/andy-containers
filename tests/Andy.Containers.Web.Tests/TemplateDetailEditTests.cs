using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Andy.Containers.Web.Pages;
using Andy.Containers.Web.Services;
using Andy.Containers.Web.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Web.Tests;

public class TemplateDetailEditTests : TestContext
{
    private readonly MockHttpMessageHandler _handler;
    private readonly Guid _templateId = Guid.NewGuid();
    private readonly TemplateDto _template;
    private readonly TemplateDefinitionDto _definition;

    public TemplateDetailEditTests()
    {
        _handler = new MockHttpMessageHandler();
        JSInterop.SetupVoid("yamlEditor.init", _ => true).SetVoidResult();
        JSInterop.SetupVoid("yamlEditor.setDiagnostics", _ => true).SetVoidResult();
        JSInterop.SetupVoid("yamlEditor.dispose", _ => true).SetVoidResult();
        JSInterop.SetupVoid("yamlEditor.setValue", _ => true).SetVoidResult();
        JSInterop.SetupVoid("yamlEditor.downloadFile", _ => true).SetVoidResult();

        _template = new TemplateDto
        {
            Id = _templateId,
            Name = "Python Dev Container",
            Code = "python-dev",
            Version = "2.0",
            BaseImage = "python:3.12",
            Description = "A Python development container",
            CatalogScope = "Global",
            IdeType = "CodeServer"
        };

        _definition = new TemplateDefinitionDto
        {
            Code = "python-dev",
            Content = "kind: Template\nname: python-dev"
        };
    }

    private void SetupDefaultHandlers()
    {
        _handler.SetupGet($"api/templates/{_templateId}", _template);
        _handler.SetupGet($"api/templates/{_templateId}/definition", _definition);
    }

    private void RegisterService()
    {
        var client = _handler.CreateClient();
        var service = new ContainersApiService(client);
        Services.AddSingleton(service);
    }

    private IRenderedComponent<TemplateDetail> RenderAndWait()
    {
        SetupDefaultHandlers();
        RegisterService();
        var cut = RenderComponent<TemplateDetail>(p => p.Add(s => s.Id, _templateId));
        cut.WaitForState(() => cut.Markup.Contains("Python Dev Container"), TimeSpan.FromSeconds(2));
        return cut;
    }

    [Fact]
    public void RendersTabBarWithDetailsEditYamlAndDependenciesTabs()
    {
        var cut = RenderAndWait();

        var tabs = cut.FindAll(".tab-item");
        tabs.Should().HaveCount(3);
        cut.Markup.Should().Contain("Details");
        cut.Markup.Should().Contain("Edit YAML");
        cut.Markup.Should().Contain("Dependencies");
    }

    [Fact]
    public void DetailsTabIsActiveByDefault()
    {
        var cut = RenderAndWait();

        var activeTab = cut.Find(".tab-item.active");
        activeTab.TextContent.Should().Contain("Details");
    }

    [Fact]
    public void ShowsOverviewCardInDetailsTab()
    {
        var cut = RenderAndWait();

        cut.Markup.Should().Contain("Python Dev Container");
        cut.Markup.Should().Contain("python-dev");
        cut.Markup.Should().Contain("2.0");
        cut.Markup.Should().Contain("python:3.12");
    }

    [Fact]
    public void ClickingEditYamlTabSwitchesContent()
    {
        var cut = RenderAndWait();

        // Click Edit YAML tab
        var editTab = cut.FindAll(".tab-item")[1];
        editTab.Click();

        // The active tab should now be Edit YAML
        var activeTab = cut.Find(".tab-item.active");
        activeTab.TextContent.Should().Contain("Edit YAML");

        // Should show Save button (edit tab content)
        cut.Markup.Should().Contain("Save");
    }

    [Fact]
    public void EditTabShowsEditableYamlEditor()
    {
        var cut = RenderAndWait();

        // Switch to edit tab
        cut.FindAll(".tab-item")[1].Click();

        // Should have a yaml editor container
        var editors = cut.FindAll(".yaml-editor-container");
        editors.Should().NotBeEmpty();
    }

    [Fact]
    public void EditTabShowsValidationPanel()
    {
        var cut = RenderAndWait();

        // Switch to edit tab
        cut.FindAll(".tab-item")[1].Click();

        // Should have a validation panel
        cut.Markup.Should().Contain("validation-panel");
    }

    [Fact]
    public void SaveButtonPresentInEditTab()
    {
        var cut = RenderAndWait();

        // Switch to edit tab
        cut.FindAll(".tab-item")[1].Click();

        cut.Markup.Should().Contain("Save");
        var buttons = cut.FindAll("button");
        buttons.Should().Contain(b => b.TextContent.Contains("Save"));
    }

    [Fact]
    public void RevertButtonPresentInEditTab()
    {
        var cut = RenderAndWait();

        // Switch to edit tab
        cut.FindAll(".tab-item")[1].Click();

        cut.Markup.Should().Contain("Revert");
        var buttons = cut.FindAll("button");
        buttons.Should().Contain(b => b.TextContent.Contains("Revert"));
    }

    [Fact]
    public void DownloadButtonPresentInEditTab()
    {
        var cut = RenderAndWait();

        // Switch to edit tab
        cut.FindAll(".tab-item")[1].Click();

        cut.Markup.Should().Contain("Download YAML");
        var buttons = cut.FindAll("button");
        buttons.Should().Contain(b => b.TextContent.Contains("Download YAML"));
    }

    [Fact]
    public void ClickingDependenciesTabShowsDependencyManager()
    {
        var cut = RenderAndWait();

        // Click Dependencies tab
        var depsTab = cut.FindAll(".tab-item")[2];
        depsTab.TextContent.Should().Contain("Dependencies");
        depsTab.Click();

        // The active tab should now be Dependencies
        var activeTab = cut.Find(".tab-item.active");
        activeTab.TextContent.Should().Contain("Dependencies");

        // Should show the dependency manager component
        cut.Markup.Should().Contain("dependency-manager");
    }
}
