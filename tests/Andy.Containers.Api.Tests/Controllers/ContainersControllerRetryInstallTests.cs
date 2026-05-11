using System.Text.Json;
using Andy.Containers.Abstractions;
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

/// <summary>
/// rivoli-ai/conductor#945 (M1.5.3). Covers the
/// <c>POST /api/containers/{id}/retry-code-assistant-install</c>
/// endpoint's preconditions + happy path.
/// </summary>
public class ContainersControllerRetryInstallTests : IDisposable
{
    private readonly Mock<IContainerService> _mockService = new();
    private readonly Mock<ICurrentUserService> _mockCurrentUser = new();
    private readonly Mock<IGitCloneService> _mockGitClone = new();
    private readonly Mock<IGitCredentialService> _mockCredentials = new();
    private readonly Mock<IGitRepositoryProbeService> _mockProbe = new();
    private readonly Mock<IOrganizationMembershipService> _mockOrgMembership = new();
    private readonly Mock<ICodeAssistantInstallExecutor> _mockExecutor = new();
    private readonly ContainersDbContext _db;
    private readonly ContainersController _controller;

    public ContainersControllerRetryInstallTests()
    {
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        _mockCurrentUser.Setup(u => u.IsAuthenticated()).Returns(true);
        _mockOrgMembership.Setup(o => o.IsMemberAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockOrgMembership.Setup(o => o.HasPermissionAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _db = InMemoryDbHelper.CreateContext();
        _controller = new ContainersController(
            _mockService.Object,
            _mockCurrentUser.Object,
            _db,
            _mockGitClone.Object,
            _mockCredentials.Object,
            _mockProbe.Object,
            _mockOrgMembership.Object);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task RetryCodeAssistantInstall_RunningContainerWithConfig_InvokesExecutor_Returns200()
    {
        var id = Guid.NewGuid();
        var container = MakeContainer(id, ContainerStatus.Running,
            codeAssistantJson: JsonSerializer.Serialize(new CodeAssistantConfig
            {
                Tool = CodeAssistantType.ClaudeCode,
                AutoStart = false,
            }));
        _mockService.Setup(s => s.GetContainerAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(container);
        _mockExecutor.Setup(e => e.RunAsync(container, It.IsAny<CodeAssistantConfig>(), It.IsAny<CancellationToken>()))
            .Callback<Container, CodeAssistantConfig, CancellationToken>((c, _, __) =>
            {
                c.CodeAssistantStatus = CodeAssistantInstallStatus.Installed;
                c.CodeAssistantStatusReason = null;
                c.CodeAssistantStatusAt = DateTime.UtcNow;
            })
            .Returns(Task.CompletedTask);

        var result = await _controller.RetryCodeAssistantInstall(id, _mockExecutor.Object, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(200);
        _mockExecutor.Verify(e => e.RunAsync(container, It.IsAny<CodeAssistantConfig>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RetryCodeAssistantInstall_NonRunningContainer_Returns422_ContainerNotRunning()
    {
        var id = Guid.NewGuid();
        var container = MakeContainer(id, ContainerStatus.Stopped, codeAssistantJson: JsonSerializer.Serialize(new CodeAssistantConfig { Tool = CodeAssistantType.ClaudeCode }));
        _mockService.Setup(s => s.GetContainerAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(container);

        var result = await _controller.RetryCodeAssistantInstall(id, _mockExecutor.Object, CancellationToken.None);

        var unproc = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        unproc.StatusCode.Should().Be(422);
        unproc.Value!.ToString().Should().Contain("container_not_running");
        _mockExecutor.Verify(e => e.RunAsync(It.IsAny<Container>(), It.IsAny<CodeAssistantConfig>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RetryCodeAssistantInstall_NoCodeAssistantConfigured_Returns422_NoConfig()
    {
        var id = Guid.NewGuid();
        var container = MakeContainer(id, ContainerStatus.Running, codeAssistantJson: null);
        _mockService.Setup(s => s.GetContainerAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(container);

        var result = await _controller.RetryCodeAssistantInstall(id, _mockExecutor.Object, CancellationToken.None);

        var unproc = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        unproc.Value!.ToString().Should().Contain("no_code_assistant_configured");
        _mockExecutor.Verify(e => e.RunAsync(It.IsAny<Container>(), It.IsAny<CodeAssistantConfig>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RetryCodeAssistantInstall_UnparseableCodeAssistantJson_Returns422_Unreadable()
    {
        var id = Guid.NewGuid();
        var container = MakeContainer(id, ContainerStatus.Running, codeAssistantJson: "not-json{");
        _mockService.Setup(s => s.GetContainerAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(container);

        var result = await _controller.RetryCodeAssistantInstall(id, _mockExecutor.Object, CancellationToken.None);

        var unproc = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        unproc.Value!.ToString().Should().Contain("code_assistant_config_unreadable");
        _mockExecutor.Verify(e => e.RunAsync(It.IsAny<Container>(), It.IsAny<CodeAssistantConfig>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RetryCodeAssistantInstall_DifferentOwner_Forbids()
    {
        var id = Guid.NewGuid();
        var container = MakeContainer(id, ContainerStatus.Running, codeAssistantJson: JsonSerializer.Serialize(new CodeAssistantConfig { Tool = CodeAssistantType.ClaudeCode }));
        container.OwnerId = "someone-else";
        _mockService.Setup(s => s.GetContainerAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(container);
        // Non-admin + not-a-member → CanAccess returns false.
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(false);
        _mockOrgMembership.Setup(o => o.IsMemberAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _controller.RetryCodeAssistantInstall(id, _mockExecutor.Object, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        _mockExecutor.Verify(e => e.RunAsync(It.IsAny<Container>(), It.IsAny<CodeAssistantConfig>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Container MakeContainer(Guid id, ContainerStatus status, string? codeAssistantJson)
    {
        return new Container
        {
            Id = id,
            Name = "test-ctr",
            OwnerId = "test-user",
            TemplateId = Guid.NewGuid(),
            ProviderId = Guid.NewGuid(),
            Status = status,
            CodeAssistant = codeAssistantJson,
        };
    }
}
