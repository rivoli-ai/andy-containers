using Andy.Containers.Abstractions;
using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

public class ContainersControllerTests : IDisposable
{
    private readonly Mock<IContainerService> _mockService;
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly Mock<IContainerPermissionService> _mockPermissions;
    private readonly Mock<ISshKeyService> _mockSshKeyService;
    private readonly ContainersDbContext _db;
    private readonly ContainersController _controller;

    public ContainersControllerTests()
    {
        _mockService = new Mock<IContainerService>();
        _mockCurrentUser = new Mock<ICurrentUserService>();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        _mockCurrentUser.Setup(u => u.IsAuthenticated()).Returns(true);
        _mockPermissions = new Mock<IContainerPermissionService>();
        _mockPermissions.Setup(p => p.HasPermissionAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _db = InMemoryDbHelper.CreateContext();
        _mockSshKeyService = new Mock<ISshKeyService>();
        var mockSshProvisioning = new Mock<ISshProvisioningService>();
        _controller = new ContainersController(_mockService.Object, _mockCurrentUser.Object, _mockPermissions.Object, _db, _mockSshKeyService.Object, mockSshProvisioning.Object);
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
        var container = new Container { Id = id, Name = "test", OwnerId = "test-user" };
        _mockService
            .Setup(s => s.GetContainerAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);
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
        var container = new Container { Id = id, Name = "test", OwnerId = "test-user" };
        var execResult = new ExecResult { ExitCode = 0, StdOut = "hello" };
        _mockService
            .Setup(s => s.GetContainerAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);
        _mockService
            .Setup(s => s.ExecAsync(id, "echo hello", It.IsAny<CancellationToken>()))
            .ReturnsAsync(execResult);

        var result = await _controller.Exec(id, new ExecRequest { Command = "echo hello" }, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().Be(execResult);
    }

    // === Story 10: SSH fields pass-through ===

    [Fact]
    public async Task Create_WithSshEnabled_PassesFlagToService()
    {
        CreateContainerRequest? capturedRequest = null;
        _mockService
            .Setup(s => s.CreateContainerAsync(It.IsAny<CreateContainerRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateContainerRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new Container { Name = "ssh-test", OwnerId = "test-user" });

        var request = new CreateContainerRequest { Name = "ssh-test", SshEnabled = true };
        await _controller.Create(request, CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.SshEnabled.Should().BeTrue();
        capturedRequest.OwnerId.Should().Be("test-user");
    }

    [Fact]
    public async Task Create_WithSshPublicKeys_PassesKeysToService()
    {
        CreateContainerRequest? capturedRequest = null;
        var keys = new[] { "ssh-ed25519 AAAA user@host", "ssh-rsa BBBB user@host" };
        _mockService
            .Setup(s => s.CreateContainerAsync(It.IsAny<CreateContainerRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateContainerRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new Container { Name = "ssh-keys-test", OwnerId = "test-user" });

        var request = new CreateContainerRequest { Name = "ssh-keys-test", SshEnabled = true, SshPublicKeys = keys };
        await _controller.Create(request, CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.SshPublicKeys.Should().BeEquivalentTo(keys);
    }

    // === Story 12: Connection endpoint SSH fields ===

    [Fact]
    public async Task GetConnectionInfo_SshEnabled_IncludesSshFields()
    {
        var id = Guid.NewGuid();
        var container = new Container { Id = id, Name = "ssh-conn", OwnerId = "test-user", SshEnabled = true };
        _mockService.Setup(s => s.GetContainerAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);
        _mockService.Setup(s => s.GetConnectionInfoAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectionInfo { SshEndpoint = "localhost:2222", SshUser = "dev" });

        var result = await _controller.GetConnectionInfo(id, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var info = ok.Value.Should().BeOfType<ConnectionInfo>().Subject;
        info.SshEndpoint.Should().Be("localhost:2222");
        info.SshUser.Should().Be("dev");
    }

    [Fact]
    public async Task GetConnectionInfo_SshNotEnabled_HasNullSshFields()
    {
        var id = Guid.NewGuid();
        var container = new Container { Id = id, Name = "no-ssh-conn", OwnerId = "test-user", SshEnabled = false };
        _mockService.Setup(s => s.GetContainerAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);
        _mockService.Setup(s => s.GetConnectionInfoAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectionInfo());

        var result = await _controller.GetConnectionInfo(id, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var info = ok.Value.Should().BeOfType<ConnectionInfo>().Subject;
        info.SshEndpoint.Should().BeNull();
        info.SshUser.Should().BeNull();
    }

    // === Story 17: container:connect permission ===

    [Fact]
    public async Task EnableSsh_NoPermission_Returns403()
    {
        var id = Guid.NewGuid();
        var container = new Container { Id = id, Name = "denied-enable", OwnerId = "other-user" };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        _mockService.Setup(s => s.GetContainerAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);
        _mockPermissions.Setup(p => p.HasPermissionAsync("test-user", id, "container:connect", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.EnableSsh(id, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task DisableSsh_NoPermission_Returns403()
    {
        var id = Guid.NewGuid();
        var container = new Container { Id = id, Name = "denied-disable", OwnerId = "other-user" };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        _mockService.Setup(s => s.GetContainerAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);
        _mockPermissions.Setup(p => p.HasPermissionAsync("test-user", id, "container:connect", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.DisableSsh(id, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    // === Story 18: Audit logging ===

    [Fact]
    public async Task EnableSsh_CreatesContainerEvent_SshEnabled()
    {
        var id = Guid.NewGuid();
        var container = new Container { Id = id, Name = "audit-enable", OwnerId = "test-user" };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        _mockService.Setup(s => s.GetContainerAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);
        _mockSshKeyService.Setup(s => s.ListKeysAsync("test-user", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserSshKey> { new() { UserId = "test-user", Label = "key", PublicKey = "ssh-ed25519 AAAA", Fingerprint = "f", KeyType = "ed25519" } });

        await _controller.EnableSsh(id, CancellationToken.None);

        var events = await _db.Events.Where(e => e.ContainerId == id).ToListAsync();
        events.Should().ContainSingle(e => e.EventType == ContainerEventType.SshEnabled);
        events.First().SubjectId.Should().Be("test-user");
    }

    [Fact]
    public async Task DisableSsh_CreatesContainerEvent_SshDisabled()
    {
        var id = Guid.NewGuid();
        var container = new Container { Id = id, Name = "audit-disable", OwnerId = "test-user" };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        _mockService.Setup(s => s.GetContainerAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        await _controller.DisableSsh(id, CancellationToken.None);

        var events = await _db.Events.Where(e => e.ContainerId == id).ToListAsync();
        events.Should().ContainSingle(e => e.EventType == ContainerEventType.SshDisabled);
        events.First().SubjectId.Should().Be("test-user");
    }
}
