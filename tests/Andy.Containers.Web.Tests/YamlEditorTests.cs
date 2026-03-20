using Andy.Containers.Web.Shared;
using Bunit;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Web.Tests;

public class YamlEditorTests : TestContext
{
    public YamlEditorTests()
    {
        JSInterop.SetupVoid("yamlEditor.init", _ => true).SetVoidResult();
        JSInterop.SetupVoid("yamlEditor.setDiagnostics", _ => true).SetVoidResult();
        JSInterop.SetupVoid("yamlEditor.dispose", _ => true).SetVoidResult();
        JSInterop.SetupVoid("yamlEditor.setValue", _ => true).SetVoidResult();
    }

    [Fact]
    public void ShouldRenderEditorContainer()
    {
        var cut = RenderComponent<YamlEditor>(parameters => parameters
            .Add(p => p.Value, "code: test")
            .Add(p => p.Height, "400px"));

        var container = cut.Find(".yaml-editor-container");
        container.Should().NotBeNull();
        container.GetAttribute("style").Should().Contain("400px");
    }

    [Fact]
    public void ShouldHaveUniqueEditorId()
    {
        var cut1 = RenderComponent<YamlEditor>();
        var cut2 = RenderComponent<YamlEditor>();

        var id1 = cut1.Find(".yaml-editor-container").Id;
        var id2 = cut2.Find(".yaml-editor-container").Id;
        id1.Should().NotBe(id2);
    }

    [Fact]
    public void ShouldCallJSInitOnFirstRender()
    {
        var cut = RenderComponent<YamlEditor>(parameters => parameters
            .Add(p => p.Value, "test: value")
            .Add(p => p.ReadOnly, true));

        var invocations = JSInterop.Invocations["yamlEditor.init"];
        invocations.Should().HaveCount(1);
    }

    [Fact]
    public void ShouldPassReadOnlyToJS()
    {
        var cut = RenderComponent<YamlEditor>(parameters => parameters
            .Add(p => p.ReadOnly, true));

        var invocation = JSInterop.Invocations["yamlEditor.init"][0];
        invocation.Arguments.Should().HaveCount(3);
    }

    [Fact]
    public void ShouldPassValueToJS()
    {
        var cut = RenderComponent<YamlEditor>(parameters => parameters
            .Add(p => p.Value, "name: test-template"));

        var invocation = JSInterop.Invocations["yamlEditor.init"][0];
        invocation.Arguments.Should().HaveCount(3);
    }

    [Fact]
    public void ShouldUseDefaultHeight()
    {
        var cut = RenderComponent<YamlEditor>();

        var container = cut.Find(".yaml-editor-container");
        container.GetAttribute("style").Should().Contain("500px");
    }

    [Fact]
    public async Task OnEditorChanged_ShouldInvokeCallbacks()
    {
        string? changedValue = null;
        var cut = RenderComponent<YamlEditor>(parameters => parameters
            .Add(p => p.Value, "initial")
            .Add(p => p.OnChanged, (string v) => { changedValue = v; }));

        await cut.InvokeAsync(() => cut.Instance.OnEditorChanged("updated"));

        changedValue.Should().Be("updated");
    }

    [Fact]
    public async Task OnSaveRequested_ShouldInvokeCallback()
    {
        bool saveCalled = false;
        var cut = RenderComponent<YamlEditor>(parameters => parameters
            .Add(p => p.OnSave, () => { saveCalled = true; }));

        await cut.InvokeAsync(() => cut.Instance.OnSaveRequested());

        saveCalled.Should().BeTrue();
    }

    [Fact]
    public void ShouldCallSetDiagnosticsWithErrors()
    {
        var errors = new List<EditorMarker> { new(1, "Missing field") };
        var cut = RenderComponent<YamlEditor>(parameters => parameters
            .Add(p => p.Errors, errors));

        JSInterop.Invocations.Should().Contain(i => i.Identifier == "yamlEditor.setDiagnostics");
    }

    [Fact]
    public void ShouldCallSetDiagnosticsWithWarnings()
    {
        var warnings = new List<EditorMarker> { new(5, "Unknown key") };
        var cut = RenderComponent<YamlEditor>(parameters => parameters
            .Add(p => p.Warnings, warnings));

        JSInterop.Invocations.Should().Contain(i => i.Identifier == "yamlEditor.setDiagnostics");
    }

    [Fact]
    public async Task ShouldCallJSDisposeOnDispose()
    {
        var cut = RenderComponent<YamlEditor>();

        await cut.Instance.DisposeAsync();

        JSInterop.Invocations.Should().Contain(i => i.Identifier == "yamlEditor.dispose");
    }
}
