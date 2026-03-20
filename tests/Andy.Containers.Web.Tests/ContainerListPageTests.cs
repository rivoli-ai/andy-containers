using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Andy.Containers.Web.Pages;
using Andy.Containers.Web.Services;
using Andy.Containers.Web.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Web.Tests;

public class ContainerListPageTests : TestContext
{
    private readonly MockHttpMessageHandler _handler;

    public ContainerListPageTests()
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

        var cut = RenderComponent<ContainerList>();

        cut.Find(".loading-spinner").Should().NotBeNull();
    }

    [Fact]
    public void ShowsContainerTableAfterDataLoads()
    {
        _handler.SetupGet("api/containers", new PagedResult<ContainerDto>
        {
            Items =
            [
                new ContainerDto { Id = Guid.NewGuid(), Name = "web-app", Status = "Running", OwnerId = "user1" },
                new ContainerDto { Id = Guid.NewGuid(), Name = "api-server", Status = "Stopped", OwnerId = "user2" }
            ],
            TotalCount = 2
        });
        RegisterService();

        var cut = RenderComponent<ContainerList>();
        cut.WaitForState(() => cut.Markup.Contains("data-table"), TimeSpan.FromSeconds(2));

        cut.Find("table.data-table").Should().NotBeNull();
        cut.Markup.Should().Contain("web-app");
        cut.Markup.Should().Contain("api-server");
    }

    [Fact]
    public void ShowsEmptyStateWhenNoContainers()
    {
        _handler.SetupGet("api/containers", new PagedResult<ContainerDto> { Items = [], TotalCount = 0 });
        RegisterService();

        var cut = RenderComponent<ContainerList>();
        cut.WaitForState(() => cut.Markup.Contains("empty-state"), TimeSpan.FromSeconds(2));

        cut.Find(".empty-state").Should().NotBeNull();
        cut.Markup.Should().Contain("No containers found.");
    }

    [Fact]
    public void ShowsNewContainerLink()
    {
        _handler.SetupGet("api/containers", new PagedResult<ContainerDto> { Items = [], TotalCount = 0 });
        RegisterService();

        var cut = RenderComponent<ContainerList>();

        var link = cut.Find("a[href='containers/create']");
        link.Should().NotBeNull();
        cut.Markup.Should().Contain("New Container");
    }

    [Fact]
    public void ShowsStartButtonForStoppedContainers()
    {
        _handler.SetupGet("api/containers", new PagedResult<ContainerDto>
        {
            Items = [new ContainerDto { Id = Guid.NewGuid(), Name = "stopped-c", Status = "Stopped" }],
            TotalCount = 1
        });
        RegisterService();

        var cut = RenderComponent<ContainerList>();
        cut.WaitForState(() => cut.Markup.Contains("data-table"), TimeSpan.FromSeconds(2));

        var startButton = cut.Find("button.btn-success[title='Start']");
        startButton.Should().NotBeNull();
    }

    [Fact]
    public void ShowsStopButtonForRunningContainers()
    {
        _handler.SetupGet("api/containers", new PagedResult<ContainerDto>
        {
            Items = [new ContainerDto { Id = Guid.NewGuid(), Name = "running-c", Status = "Running" }],
            TotalCount = 1
        });
        RegisterService();

        var cut = RenderComponent<ContainerList>();
        cut.WaitForState(() => cut.Markup.Contains("data-table"), TimeSpan.FromSeconds(2));

        var stopButton = cut.Find("button.btn-warning[title='Stop']");
        stopButton.Should().NotBeNull();
    }

    [Fact]
    public void ShowsDestroyButtonForNonDestroyedContainers()
    {
        _handler.SetupGet("api/containers", new PagedResult<ContainerDto>
        {
            Items = [new ContainerDto { Id = Guid.NewGuid(), Name = "active-c", Status = "Running" }],
            TotalCount = 1
        });
        RegisterService();

        var cut = RenderComponent<ContainerList>();
        cut.WaitForState(() => cut.Markup.Contains("data-table"), TimeSpan.FromSeconds(2));

        var destroyButton = cut.Find("button.btn-danger[title='Destroy']");
        destroyButton.Should().NotBeNull();
    }

    [Fact]
    public void ShowsErrorAlertWhenApiFails()
    {
        _handler.SetupError("api/containers", HttpStatusCode.InternalServerError);
        RegisterService();

        var cut = RenderComponent<ContainerList>();
        cut.WaitForState(() => cut.Markup.Contains("alert-error"), TimeSpan.FromSeconds(2));

        cut.Find(".alert-error").Should().NotBeNull();
        cut.Markup.Should().Contain("Failed to load containers");
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
