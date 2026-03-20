using System.Net;
using Andy.Containers.Web.Services;
using Andy.Containers.Web.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Web.Tests;

public class ContainersApiServiceTests
{
    private readonly MockHttpMessageHandler _handler;
    private readonly ContainersApiService _sut;

    public ContainersApiServiceTests()
    {
        _handler = new MockHttpMessageHandler();
        var httpClient = _handler.CreateClient();
        _sut = new ContainersApiService(httpClient);
    }

    // ---------- GetContainersAsync ----------

    [Fact]
    public async Task GetContainersAsync_ReturnsPagedResults()
    {
        var expected = new PagedResult<ContainerDto>
        {
            Items = [new ContainerDto { Id = Guid.NewGuid(), Name = "test-1" }],
            TotalCount = 1
        };
        _handler.SetupGet("api/containers", expected);

        var result = await _sut.GetContainersAsync();

        result.Items.Should().HaveCount(1);
        result.Items[0].Name.Should().Be("test-1");
        result.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task GetContainersAsync_ReturnsEmptyWhenNoData()
    {
        var expected = new PagedResult<ContainerDto> { Items = [], TotalCount = 0 };
        _handler.SetupGet("api/containers", expected);

        var result = await _sut.GetContainersAsync();

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    // ---------- GetContainerAsync ----------

    [Fact]
    public async Task GetContainerAsync_ReturnsContainer()
    {
        var id = Guid.NewGuid();
        var expected = new ContainerDto { Id = id, Name = "my-container" };
        _handler.SetupGet($"api/containers/{id}", expected);

        var result = await _sut.GetContainerAsync(id);

        result.Should().NotBeNull();
        result!.Name.Should().Be("my-container");
        result.Id.Should().Be(id);
    }

    [Fact]
    public async Task GetContainerAsync_WithBadId_ThrowsHttpRequestException()
    {
        var id = Guid.NewGuid();
        _handler.SetupError($"api/containers/{id}", HttpStatusCode.NotFound);

        var act = () => _sut.GetContainerAsync(id);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // ---------- GetTemplatesAsync ----------

    [Fact]
    public async Task GetTemplatesAsync_WithSearchParameter()
    {
        var expected = new PagedResult<TemplateDto>
        {
            Items = [new TemplateDto { Id = Guid.NewGuid(), Name = "Python Dev" }],
            TotalCount = 1
        };
        _handler.SetupGet("api/templates", expected);

        var result = await _sut.GetTemplatesAsync(search: "Python");

        result.Items.Should().HaveCount(1);
        result.Items[0].Name.Should().Be("Python Dev");

        var request = _handler.Requests.Last();
        request.RequestUri!.ToString().Should().Contain("search=Python");
    }

    [Fact]
    public async Task GetTemplatesAsync_WithoutSearch()
    {
        var expected = new PagedResult<TemplateDto>
        {
            Items = [new TemplateDto { Id = Guid.NewGuid(), Name = "Go Dev" }],
            TotalCount = 1
        };
        _handler.SetupGet("api/templates", expected);

        var result = await _sut.GetTemplatesAsync();

        result.Items.Should().HaveCount(1);
        var request = _handler.Requests.Last();
        request.RequestUri!.ToString().Should().NotContain("search=");
    }

    // ---------- GetTemplateDefinitionAsync ----------

    [Fact]
    public async Task GetTemplateDefinitionAsync_ReturnsNullOn404()
    {
        var id = Guid.NewGuid();
        _handler.SetupError($"api/templates/{id}/definition", HttpStatusCode.NotFound);

        var result = await _sut.GetTemplateDefinitionAsync(id);

        result.Should().BeNull();
    }

    // ---------- GetProvidersAsync ----------

    [Fact]
    public async Task GetProvidersAsync_ReturnsList()
    {
        var expected = new List<ProviderDto>
        {
            new() { Id = Guid.NewGuid(), Name = "Docker Local" },
            new() { Id = Guid.NewGuid(), Name = "AWS ECS" }
        };
        _handler.SetupGet("api/providers", expected);

        var result = await _sut.GetProvidersAsync();

        result.Should().HaveCount(2);
        result[0].Name.Should().Be("Docker Local");
        result[1].Name.Should().Be("AWS ECS");
    }

    // ---------- GetWorkspacesAsync ----------

    [Fact]
    public async Task GetWorkspacesAsync_ReturnsPagedResults()
    {
        var expected = new PagedResult<WorkspaceDto>
        {
            Items = [new WorkspaceDto { Id = Guid.NewGuid(), Name = "ws-1" }],
            TotalCount = 5
        };
        _handler.SetupGet("api/workspaces", expected);

        var result = await _sut.GetWorkspacesAsync();

        result.Items.Should().HaveCount(1);
        result.TotalCount.Should().Be(5);
    }

    // ---------- StartContainerAsync ----------

    [Fact]
    public async Task StartContainerAsync_CallsPost()
    {
        var id = Guid.NewGuid();
        _handler.SetupPost($"api/containers/{id}/start");

        await _sut.StartContainerAsync(id);

        var request = _handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Contain($"api/containers/{id}/start");
    }

    // ---------- StopContainerAsync ----------

    [Fact]
    public async Task StopContainerAsync_CallsPost()
    {
        var id = Guid.NewGuid();
        _handler.SetupPost($"api/containers/{id}/stop");

        await _sut.StopContainerAsync(id);

        var request = _handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Contain($"api/containers/{id}/stop");
    }

    // ---------- DestroyContainerAsync ----------

    [Fact]
    public async Task DestroyContainerAsync_CallsDelete()
    {
        var id = Guid.NewGuid();
        _handler.SetupDelete($"api/containers/{id}");

        await _sut.DestroyContainerAsync(id);

        var request = _handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Delete);
        request.RequestUri!.ToString().Should().Contain($"api/containers/{id}");
    }

    // ---------- CreateContainerAsync ----------

    [Fact]
    public async Task CreateContainerAsync_PostsAndReturnsResult()
    {
        var expected = new ContainerDto { Id = Guid.NewGuid(), Name = "new-container" };
        _handler.SetupPost("api/containers", expected);

        var request = new CreateContainerRequest { Name = "new-container" };
        var result = await _sut.CreateContainerAsync(request);

        result.Should().NotBeNull();
        result!.Name.Should().Be("new-container");

        var httpRequest = _handler.Requests.Should().ContainSingle().Subject;
        httpRequest.Method.Should().Be(HttpMethod.Post);
    }
}
