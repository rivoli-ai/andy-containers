using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Andy.Containers.Web.Pages;
using Andy.Containers.Web.Services;
using Andy.Containers.Web.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Web.Tests;

public class TemplateCatalogPageTests : TestContext
{
    private readonly MockHttpMessageHandler _handler;

    public TemplateCatalogPageTests()
    {
        _handler = new MockHttpMessageHandler();
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

        var cut = RenderComponent<TemplateCatalog>();

        cut.Find(".loading-spinner").Should().NotBeNull();
    }

    [Fact]
    public void ShowsTemplateCardsAfterDataLoads()
    {
        _handler.SetupGet("api/templates", new PagedResult<TemplateDto>
        {
            Items =
            [
                new TemplateDto { Id = Guid.NewGuid(), Name = "Python Dev", Code = "python", Version = "1.0", BaseImage = "python:3.12", CatalogScope = "Global", IdeType = "CodeServer" },
                new TemplateDto { Id = Guid.NewGuid(), Name = "Go Dev", Code = "go", Version = "1.0", BaseImage = "golang:1.22", CatalogScope = "Global", IdeType = "CodeServer" }
            ],
            TotalCount = 2
        });
        RegisterService();

        var cut = RenderComponent<TemplateCatalog>();
        cut.WaitForState(() => cut.Markup.Contains("card-grid"), TimeSpan.FromSeconds(2));

        var cards = cut.FindAll(".card");
        cards.Should().HaveCount(2);
        cut.Markup.Should().Contain("Python Dev");
        cut.Markup.Should().Contain("Go Dev");
    }

    [Fact]
    public void ShowsEmptyStateWhenNoTemplates()
    {
        _handler.SetupGet("api/templates", new PagedResult<TemplateDto> { Items = [], TotalCount = 0 });
        RegisterService();

        var cut = RenderComponent<TemplateCatalog>();
        cut.WaitForState(() => cut.Markup.Contains("empty-state"), TimeSpan.FromSeconds(2));

        cut.Find(".empty-state").Should().NotBeNull();
    }

    [Fact]
    public void ShowsSearchInput()
    {
        _handler.SetupGet("api/templates", new PagedResult<TemplateDto> { Items = [], TotalCount = 0 });
        RegisterService();

        var cut = RenderComponent<TemplateCatalog>();

        cut.Find("input.search-input").Should().NotBeNull();
    }

    [Fact]
    public void ShowsPaginationInfoWhenMoreResultsExist()
    {
        _handler.SetupGet("api/templates", new PagedResult<TemplateDto>
        {
            Items =
            [
                new TemplateDto { Id = Guid.NewGuid(), Name = "Template 1", Code = "t1", Version = "1.0", BaseImage = "img", CatalogScope = "Global", IdeType = "CodeServer" }
            ],
            TotalCount = 100
        });
        RegisterService();

        var cut = RenderComponent<TemplateCatalog>();
        cut.WaitForState(() => cut.Markup.Contains("pagination-info"), TimeSpan.FromSeconds(2));

        cut.Find(".pagination-info").Should().NotBeNull();
        cut.Markup.Should().Contain("Showing 1 of 100 templates.");
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
