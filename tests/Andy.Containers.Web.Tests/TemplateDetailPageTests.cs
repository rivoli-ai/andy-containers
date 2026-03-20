using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Andy.Containers.Web.Pages;
using Andy.Containers.Web.Services;
using Andy.Containers.Web.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Web.Tests;

public class TemplateDetailPageTests : TestContext
{
    private readonly MockHttpMessageHandler _handler;
    private readonly Guid _templateId = Guid.NewGuid();

    public TemplateDetailPageTests()
    {
        _handler = new MockHttpMessageHandler();
        JSInterop.SetupVoid("yamlEditor.init", _ => true).SetVoidResult();
        JSInterop.SetupVoid("yamlEditor.setDiagnostics", _ => true).SetVoidResult();
        JSInterop.SetupVoid("yamlEditor.dispose", _ => true).SetVoidResult();
        JSInterop.SetupVoid("yamlEditor.setValue", _ => true).SetVoidResult();
    }

    private void RegisterService()
    {
        var client = _handler.CreateClient();
        var service = new ContainersApiService(client);
        Services.AddSingleton(service);
    }

    [Fact]
    public void ShowsLoadingSpinnerInitially()
    {
        var handler = new DelayedHttpMessageHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        Services.AddSingleton(new ContainersApiService(client));

        var cut = RenderComponent<TemplateDetail>(p => p.Add(s => s.Id, _templateId));

        cut.Find(".loading-spinner").Should().NotBeNull();
    }

    [Fact]
    public void ShowsTemplateDetailsAfterDataLoads()
    {
        var template = new TemplateDto
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
        _handler.SetupGet($"api/templates/{_templateId}", template);
        _handler.SetupGet($"api/templates/{_templateId}/definition",
            new TemplateDefinitionDto { Code = "python-dev", Content = "kind: Template\nname: python-dev" });
        RegisterService();

        var cut = RenderComponent<TemplateDetail>(p => p.Add(s => s.Id, _templateId));
        cut.WaitForState(() => cut.Markup.Contains("Python Dev Container"), TimeSpan.FromSeconds(2));

        cut.Markup.Should().Contain("Python Dev Container");
        cut.Markup.Should().Contain("python-dev");
        cut.Markup.Should().Contain("2.0");
        cut.Markup.Should().Contain("python:3.12");
    }

    [Fact]
    public void ShowsNotFoundErrorWhenTemplateIsNull()
    {
        // GetTemplateAsync uses GetFromJsonAsync which on 404 throws HttpRequestException.
        // But if the server returns 200 with null body, the method returns null.
        // The page then sets _error = "Template not found."
        // We need to simulate a null response. SetupGet with a null-like response:
        _handler.SetupError($"api/templates/{_templateId}", HttpStatusCode.NotFound);
        RegisterService();

        var cut = RenderComponent<TemplateDetail>(p => p.Add(s => s.Id, _templateId));
        cut.WaitForState(() => cut.Markup.Contains("alert-error"), TimeSpan.FromSeconds(2));

        // The page catches the HttpRequestException and shows its message
        cut.Find(".alert-error").Should().NotBeNull();
    }

    [Fact]
    public void ShowsYamlDefinitionWhenLoaded()
    {
        var template = new TemplateDto
        {
            Id = _templateId,
            Name = "Go Dev",
            Code = "go-dev",
            Version = "1.0",
            BaseImage = "golang:1.22",
            CatalogScope = "Global",
            IdeType = "CodeServer"
        };
        var definition = new TemplateDefinitionDto
        {
            Code = "go-dev",
            Content = "kind: Template\nname: go-dev\nbaseImage: golang:1.22"
        };
        _handler.SetupGet($"api/templates/{_templateId}", template);
        _handler.SetupGet($"api/templates/{_templateId}/definition", definition);
        RegisterService();

        var cut = RenderComponent<TemplateDetail>(p => p.Add(s => s.Id, _templateId));
        cut.WaitForState(() => cut.Markup.Contains("yaml-editor-container"), TimeSpan.FromSeconds(2));

        var editor = cut.Find(".yaml-editor-container");
        editor.Should().NotBeNull();
        // The YamlEditor component passes value via JS interop, so verify init was called
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "yamlEditor.init");
    }

    private class DelayedHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
    }
}
