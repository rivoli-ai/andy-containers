using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

public class WorkspacesControllerTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly WorkspacesController _controller;

    public WorkspacesControllerTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockCurrentUser = new Mock<ICurrentUserService>();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        _mockCurrentUser.Setup(u => u.IsAuthenticated()).Returns(true);
        _controller = new WorkspacesController(_db, _mockCurrentUser.Object);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task Create_ShouldReturnCreatedWorkspace()
    {
        var dto = new CreateWorkspaceDto("My Workspace", "A test workspace", null, null, "https://github.com/test/repo", "main");

        var result = await _controller.Create(dto, CancellationToken.None);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(201);
        var ws = created.Value.Should().BeOfType<Workspace>().Subject;
        ws.Name.Should().Be("My Workspace");
        ws.OwnerId.Should().Be("test-user");
        ws.GitRepositoryUrl.Should().Be("https://github.com/test/repo");
        ws.GitBranch.Should().Be("main");
    }

    [Fact]
    public async Task Get_ExistingWorkspace_ShouldReturnOk()
    {
        var workspace = new Workspace { Name = "WS1", OwnerId = "user1" };
        _db.Workspaces.Add(workspace);
        await _db.SaveChangesAsync();

        var result = await _controller.Get(workspace.Id, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<Workspace>().Subject;
        returned.Name.Should().Be("WS1");
    }

    [Fact]
    public async Task Get_NonExistentWorkspace_ShouldReturnNotFound()
    {
        var result = await _controller.Get(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task List_ShouldFilterByOwnerId()
    {
        _db.Workspaces.AddRange(
            new Workspace { Name = "WS-A", OwnerId = "user1" },
            new Workspace { Name = "WS-B", OwnerId = "user2" },
            new Workspace { Name = "WS-C", OwnerId = "user1" }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.List(ownerId: "user1", organizationId: null, status: null);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var value = okResult.Value!;
        var totalCount = (int)value.GetType().GetProperty("totalCount")!.GetValue(value)!;
        totalCount.Should().Be(2);
    }

    [Fact]
    public async Task Update_ShouldModifyFields()
    {
        var workspace = new Workspace { Name = "Original", OwnerId = "user1", Description = "Old description" };
        _db.Workspaces.Add(workspace);
        await _db.SaveChangesAsync();

        var dto = new UpdateWorkspaceDto("Updated", "New description", "feature-branch");
        var result = await _controller.Update(workspace.Id, dto, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var updated = okResult.Value.Should().BeOfType<Workspace>().Subject;
        updated.Name.Should().Be("Updated");
        updated.Description.Should().Be("New description");
        updated.GitBranch.Should().Be("feature-branch");
        updated.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Update_NonExistentWorkspace_ShouldReturnNotFound()
    {
        var dto = new UpdateWorkspaceDto("name", null, null);
        var result = await _controller.Update(Guid.NewGuid(), dto, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Delete_ExistingWorkspace_ShouldRemoveAndReturnNoContent()
    {
        var workspace = new Workspace { Name = "ToDelete", OwnerId = "user1" };
        _db.Workspaces.Add(workspace);
        await _db.SaveChangesAsync();

        var result = await _controller.Delete(workspace.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        var found = await _db.Workspaces.FindAsync(workspace.Id);
        found.Should().BeNull();
    }

    [Fact]
    public async Task Delete_NonExistentWorkspace_ShouldReturnNotFound()
    {
        var result = await _controller.Delete(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }
}
