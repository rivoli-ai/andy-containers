using Andy.Containers.Abstractions;
using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

public class ContainersControllerTests : IDisposable
{
    private readonly Mock<IContainerService> _mockService;
    private readonly ContainersDbContext _db;
    private readonly ContainersController _controller;

    public ContainersControllerTests()
    {
        _mockService = new Mock<IContainerService>();
        _db = InMemoryDbHelper.CreateContext();
        _controller = new ContainersController(_mockService.Object, _db);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task List_ShouldReturnOkWithContainers()
    {
        var containers = new List<Container>
        {
            new() { Name = "c1", OwnerId = "user1" },
            new() { Name = "c2", OwnerId = "user1" }
        };
        _mockService
            .Setup(s => s.ListContainersAsync(It.IsAny<ContainerFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(containers);

        var result = await _controller.List(ownerId: "user1", organizationId: null, teamId: null,
            workspaceId: null, status: null, templateId: null, providerId: null);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Get_ExistingContainer_ShouldReturnOk()
    {
        var id = Guid.NewGuid();
        var container = new Container { Id = id, Name = "test", OwnerId = "user1" };
        _mockService
            .Setup(s => s.GetContainerAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        var result = await _controller.Get(id, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().Be(container);
    }

    [Fact]
    public async Task Get_NonExistentContainer_ShouldReturnNotFound()
    {
        var id = Guid.NewGuid();
        _mockService
            .Setup(s => s.GetContainerAsync(id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await _controller.Get(id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Create_ShouldReturnCreatedAtAction()
    {
        var request = new CreateContainerRequest { Name = "new-container" };
        var container = new Container { Id = Guid.NewGuid(), Name = "new-container", OwnerId = "system" };
        _mockService
            .Setup(s => s.CreateContainerAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        var result = await _controller.Create(request, CancellationToken.None);

        var createdResult = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.StatusCode.Should().Be(201);
        createdResult.Value.Should().Be(container);
        createdResult.ActionName.Should().Be(nameof(ContainersController.Get));
    }

    [Fact]
    public async Task Start_ShouldCallServiceAndReturnContainer()
    {
        var id = Guid.NewGuid();
        var container = new Container { Id = id, Name = "test", OwnerId = "user1", Status = ContainerStatus.Running };
        _mockService
            .Setup(s => s.StartContainerAsync(id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockService
            .Setup(s => s.GetContainerAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        var result = await _controller.Start(id, CancellationToken.None);

        _mockService.Verify(s => s.StartContainerAsync(id, It.IsAny<CancellationToken>()), Times.Once);
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Stop_ShouldCallServiceAndReturnContainer()
    {
        var id = Guid.NewGuid();
        var container = new Container { Id = id, Name = "test", OwnerId = "user1", Status = ContainerStatus.Stopped };
        _mockService
            .Setup(s => s.StopContainerAsync(id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockService
            .Setup(s => s.GetContainerAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        var result = await _controller.Stop(id, CancellationToken.None);

        _mockService.Verify(s => s.StopContainerAsync(id, It.IsAny<CancellationToken>()), Times.Once);
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Destroy_ShouldReturnNoContent()
    {
        var id = Guid.NewGuid();
        _mockService
            .Setup(s => s.DestroyContainerAsync(id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _controller.Destroy(id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _mockService.Verify(s => s.DestroyContainerAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Exec_ShouldReturnExecResult()
    {
        var id = Guid.NewGuid();
        var execResult = new ExecResult { ExitCode = 0, StdOut = "hello" };
        _mockService
            .Setup(s => s.ExecAsync(id, "echo hello", It.IsAny<CancellationToken>()))
            .ReturnsAsync(execResult);

        var result = await _controller.Exec(id, new ExecRequest { Command = "echo hello" }, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().Be(execResult);
    }
}
