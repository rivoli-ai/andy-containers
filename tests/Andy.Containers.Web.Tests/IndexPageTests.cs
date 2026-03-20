using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Andy.Containers.Web.Services;
using Andy.Containers.Web.Tests.Helpers;
using FluentAssertions;
using Xunit;
using IndexPage = Andy.Containers.Web.Pages.Index;

namespace Andy.Containers.Web.Tests;

public class IndexPageTests : TestContext
{
    private readonly MockHttpMessageHandler _handler;

    public IndexPageTests()
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
        var service = new ContainersApiService(client);
        Services.AddSingleton(service);

        var cut = RenderComponent<IndexPage>();

        cut.Find(".loading-spinner").Should().NotBeNull();
    }

    [Fact]
    public void ShowsStatsGridAfterDataLoads()
    {
        SetupSuccessResponses();
        RegisterService();

        var cut = RenderComponent<IndexPage>();
        cut.WaitForState(() => cut.Markup.Contains("stat-card"), TimeSpan.FromSeconds(2));

        var statCards = cut.FindAll(".stat-card");
        statCards.Should().HaveCount(4);

        var markup = cut.Markup;
        markup.Should().Contain("3"); // container count
        markup.Should().Contain("2"); // provider count
    }

    [Fact]
    public void ShowsErrorAlertWhenApiFails()
    {
        _handler.SetupError("api/containers", HttpStatusCode.InternalServerError);
        RegisterService();

        var cut = RenderComponent<IndexPage>();
        cut.WaitForState(() => cut.Markup.Contains("alert-error"), TimeSpan.FromSeconds(2));

        cut.Find(".alert-error").Should().NotBeNull();
    }

    [Fact]
    public void ShowsRecentContainersTableWhenContainersExist()
    {
        SetupSuccessResponses();
        RegisterService();

        var cut = RenderComponent<IndexPage>();
        cut.WaitForState(() => cut.Markup.Contains("data-table"), TimeSpan.FromSeconds(2));

        cut.Find("table.data-table").Should().NotBeNull();
        cut.Markup.Should().Contain("my-container");
    }

    [Fact]
    public void ShowsNoRecentContainersSectionWhenEmpty()
    {
        _handler.SetupGet("api/containers", new PagedResult<ContainerDto> { Items = [], TotalCount = 0 });
        _handler.SetupGet("api/providers", new List<ProviderDto>());
        _handler.SetupGet("api/templates", new PagedResult<TemplateDto> { Items = [], TotalCount = 0 });
        _handler.SetupGet("api/workspaces", new PagedResult<WorkspaceDto> { Items = [], TotalCount = 0 });
        RegisterService();

        var cut = RenderComponent<IndexPage>();
        cut.WaitForState(() => cut.Markup.Contains("stat-card"), TimeSpan.FromSeconds(2));

        cut.Markup.Should().NotContain("data-table");
        cut.Markup.Should().NotContain("Recent Containers");
    }

    private void SetupSuccessResponses()
    {
        _handler.SetupGet("api/containers", new PagedResult<ContainerDto>
        {
            Items = [new ContainerDto { Id = Guid.NewGuid(), Name = "my-container", Status = "Running" }],
            TotalCount = 3
        });
        _handler.SetupGet("api/providers", new List<ProviderDto>
        {
            new() { Id = Guid.NewGuid(), Name = "Docker" },
            new() { Id = Guid.NewGuid(), Name = "AWS" }
        });
        _handler.SetupGet("api/templates", new PagedResult<TemplateDto>
        {
            Items = [new TemplateDto { Id = Guid.NewGuid(), Name = "Python" }],
            TotalCount = 5
        });
        _handler.SetupGet("api/workspaces", new PagedResult<WorkspaceDto>
        {
            Items = [],
            TotalCount = 10
        });
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
