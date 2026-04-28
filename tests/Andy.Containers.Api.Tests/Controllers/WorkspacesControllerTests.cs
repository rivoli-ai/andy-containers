using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

public class WorkspacesControllerTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly Mock<IAgentCapabilityService> _agentCapabilities;
    private readonly WorkspacesController _controller;

    public WorkspacesControllerTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockCurrentUser = new Mock<ICurrentUserService>();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        _mockCurrentUser.Setup(u => u.IsAuthenticated()).Returns(true);
        var mockOrgMembership = new Mock<IOrganizationMembershipService>();
        mockOrgMembership.Setup(o => o.IsMemberAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mockOrgMembership.Setup(o => o.HasPermissionAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        // X9 (#99): default to "no allowlist on record" so existing tests
        // (which don't pass an AgentId) skip the enforcement branch.
        _agentCapabilities = new Mock<IAgentCapabilityService>();
        _agentCapabilities
            .Setup(a => a.GetAllowedEnvironmentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>?)null);
        _controller = new WorkspacesController(
            _db, _mockCurrentUser.Object, mockOrgMembership.Object,
            _agentCapabilities.Object,
            NullLogger<WorkspacesController>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task Create_ShouldReturnCreatedWorkspace()
    {
        // X5 (#94): EnvironmentProfileCode is now required at create time.
        var profile = await SeedHeadlessProfile();
        var dto = new CreateWorkspaceDto(
            "My Workspace", "A test workspace", null, null,
            "https://github.com/test/repo", "main",
            EnvironmentProfileCode: profile.Name);

        var result = await _controller.Create(dto, CancellationToken.None);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(201);
        var ws = created.Value.Should().BeOfType<Workspace>().Subject;
        ws.Name.Should().Be("My Workspace");
        ws.OwnerId.Should().Be("test-user");
        ws.GitRepositoryUrl.Should().Be("https://github.com/test/repo");
        ws.GitBranch.Should().Be("main");
        ws.EnvironmentProfileId.Should().Be(profile.Id,
            "the bound profile is the workspace's governance anchor");
    }

    // X5 (#94) -----------------------------------------------------------

    [Fact]
    public async Task Create_MissingEnvironmentProfileCode_ReturnsBadRequest()
    {
        var dto = new CreateWorkspaceDto(
            "no-profile", null, null, null, null, null);

        var result = await _controller.Create(dto, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>(
            "profile binding is the governance anchor; missing it can't fall through silently");
    }

    [Fact]
    public async Task Create_BlankEnvironmentProfileCode_ReturnsBadRequest()
    {
        var dto = new CreateWorkspaceDto(
            "blank", null, null, null, null, null,
            EnvironmentProfileCode: "   ");

        var result = await _controller.Create(dto, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_UnknownEnvironmentProfileCode_ReturnsBadRequest()
    {
        var dto = new CreateWorkspaceDto(
            "unknown-profile", null, null, null, null, null,
            EnvironmentProfileCode: "totally-fake");

        var result = await _controller.Create(dto, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.ToString().Should().Contain("totally-fake");
    }

    [Fact]
    public async Task Update_DoesNotExposeProfileChange()
    {
        // UpdateWorkspaceDto deliberately omits EnvironmentProfileCode —
        // the FK is a governance anchor that should require workspace
        // recreation to change. Pin that the field can't be sneaked
        // through via Update by asserting the profile id is preserved.
        var profile = await SeedHeadlessProfile();
        var ws = new Workspace
        {
            Name = "ws", OwnerId = "test-user",
            EnvironmentProfileId = profile.Id,
        };
        _db.Workspaces.Add(ws);
        await _db.SaveChangesAsync();

        var dto = new UpdateWorkspaceDto("renamed", null, null);
        var result = await _controller.Update(ws.Id, dto, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var reloaded = await _db.Workspaces.FindAsync(ws.Id);
        reloaded!.EnvironmentProfileId.Should().Be(profile.Id,
            "Update has no profile field — re-binding requires workspace recreation");
    }

    // X9 (#99) -----------------------------------------------------------
    // Agent allowed_environments enforcement. Workspaces that target a
    // specific agent must respect that agent's policy; agents without a
    // policy stay open. Service outage fails closed.

    [Fact]
    public async Task Create_AgentBound_ProfileInAllowlist_Returns201()
    {
        var profile = await SeedHeadlessProfile();
        _agentCapabilities
            .Setup(a => a.GetAllowedEnvironmentsAsync("triage", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "headless-container", "terminal" });

        var dto = new CreateWorkspaceDto(
            "agent-allowed", null, null, null, null, null,
            EnvironmentProfileCode: profile.Name,
            AgentId: "triage");

        var result = await _controller.Create(dto, CancellationToken.None);

        result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Create_AgentBound_ProfileNotInAllowlist_Returns403()
    {
        var profile = await SeedHeadlessProfile();
        _agentCapabilities
            .Setup(a => a.GetAllowedEnvironmentsAsync("review-only", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "terminal", "desktop" });

        var dto = new CreateWorkspaceDto(
            "agent-rejected", null, null, null, null, null,
            EnvironmentProfileCode: profile.Name,
            AgentId: "review-only");

        var result = await _controller.Create(dto, CancellationToken.None);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        status.Value!.ToString().Should().Contain("headless-container")
            .And.Contain("review-only",
                "the 403 must name the profile and the agent so callers can self-correct");
    }

    [Fact]
    public async Task Create_AgentBound_AllowlistMatchesCaseInsensitively()
    {
        // Agents may declare codes with different casing; the catalog
        // is canonical lowercase, so the comparison must be tolerant.
        var profile = await SeedHeadlessProfile();
        _agentCapabilities
            .Setup(a => a.GetAllowedEnvironmentsAsync("ci", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "Headless-Container" });

        var dto = new CreateWorkspaceDto(
            "casey", null, null, null, null, null,
            EnvironmentProfileCode: profile.Name,
            AgentId: "ci");

        var result = await _controller.Create(dto, CancellationToken.None);

        result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Create_AgentBound_NullAllowlist_Returns201()
    {
        // Null = "no policy on record". Open by default until an
        // agent declares an allowlist.
        var profile = await SeedHeadlessProfile();
        _agentCapabilities
            .Setup(a => a.GetAllowedEnvironmentsAsync("freeform", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>?)null);

        var dto = new CreateWorkspaceDto(
            "freeform-ws", null, null, null, null, null,
            EnvironmentProfileCode: profile.Name,
            AgentId: "freeform");

        var result = await _controller.Create(dto, CancellationToken.None);

        result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Create_AgentBound_ServiceUnavailable_Returns503()
    {
        // Fail-closed: refuse to provision a workspace against an
        // unverifiable policy rather than silently bypass enforcement.
        var profile = await SeedHeadlessProfile();
        _agentCapabilities
            .Setup(a => a.GetAllowedEnvironmentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AgentCapabilityServiceUnavailableException(
                "andy-agents 503"));

        var dto = new CreateWorkspaceDto(
            "outage", null, null, null, null, null,
            EnvironmentProfileCode: profile.Name,
            AgentId: "any");

        var result = await _controller.Create(dto, CancellationToken.None);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        (await _db.Workspaces.AnyAsync()).Should().BeFalse(
            "the service-down path must not produce a row that survived the failed check");
    }

    [Fact]
    public async Task Create_NoAgentBound_SkipsEnforcementCallEntirely()
    {
        // The capability service never gets called when AgentId is null;
        // pin that explicitly so a future refactor doesn't accidentally
        // ask the agent service "does null have an allowlist?" on every
        // workspace-create.
        var profile = await SeedHeadlessProfile();

        var dto = new CreateWorkspaceDto(
            "no-agent", null, null, null, null, null,
            EnvironmentProfileCode: profile.Name);

        var result = await _controller.Create(dto, CancellationToken.None);

        result.Should().BeOfType<CreatedAtActionResult>();
        _agentCapabilities.Verify(
            a => a.GetAllowedEnvironmentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private async Task<Andy.Containers.Models.EnvironmentProfile> SeedHeadlessProfile()
    {
        var profile = new Andy.Containers.Models.EnvironmentProfile
        {
            Id = Guid.NewGuid(),
            Name = "headless-container",
            DisplayName = "Headless container",
            Kind = Andy.Containers.Models.EnvironmentKind.HeadlessContainer,
            BaseImageRef = "ghcr.io/rivoli-ai/andy-headless:latest",
        };
        _db.EnvironmentProfiles.Add(profile);
        await _db.SaveChangesAsync();
        return profile;
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
