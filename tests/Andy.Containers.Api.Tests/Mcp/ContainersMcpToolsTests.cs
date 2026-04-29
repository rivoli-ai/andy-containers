using Andy.Containers.Abstractions;
using Andy.Containers.Api.Mcp;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Mcp;

public class ContainersMcpToolsTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IContainerService> _mockContainerService;
    private readonly ContainersMcpTools _tools;

    public ContainersMcpToolsTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockContainerService = new Mock<IContainerService>();
        var mockCurrentUser = new Mock<ICurrentUserService>();
        mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        var mockOrgMembership = new Mock<IOrganizationMembershipService>();
        mockOrgMembership.Setup(o => o.IsMemberAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mockOrgMembership.Setup(o => o.HasPermissionAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _tools = new ContainersMcpTools(_db, _mockContainerService.Object, new Mock<IGitCloneService>().Object, new Mock<IGitCredentialService>().Object, new Mock<IGitRepositoryProbeService>().Object, new Mock<IImageManifestService>().Object, new Mock<IImageDiffService>().Object, mockCurrentUser.Object, mockOrgMembership.Object, new Mock<IApiKeyService>().Object);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task ListContainers_NoContainers_ShouldReturnEmpty()
    {
        _mockContainerService.Setup(s => s.ListContainersAsync(It.IsAny<ContainerFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Container>());

        var result = await _tools.ListContainers();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListContainers_WithContainers_ShouldReturnAll()
    {
        var template = new ContainerTemplate { Code = "full-stack", Name = "Full Stack", Version = "1.0.0", BaseImage = "ubuntu:24.04" };
        var provider = new InfrastructureProvider { Code = "docker-local", Name = "Local Docker", Type = ProviderType.Docker };
        var containers = new List<Container>
        {
            new() { Name = "c1", OwnerId = "user1", Template = template, Provider = provider, Status = ContainerStatus.Running },
            new() { Name = "c2", OwnerId = "user1", Template = template, Provider = provider, Status = ContainerStatus.Stopped }
        };
        _mockContainerService.Setup(s => s.ListContainersAsync(It.IsAny<ContainerFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(containers);

        var result = await _tools.ListContainers();

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListContainers_WithStatusFilter_ShouldFilterCorrectly()
    {
        var template = new ContainerTemplate { Code = "full-stack", Name = "Full Stack", Version = "1.0.0", BaseImage = "ubuntu:24.04" };
        var provider = new InfrastructureProvider { Code = "docker-local", Name = "Local Docker", Type = ProviderType.Docker };
        _mockContainerService.Setup(s => s.ListContainersAsync(
                It.Is<ContainerFilter>(f => f.Status == ContainerStatus.Running), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Container>
            {
                new() { Name = "running1", OwnerId = "user1", Template = template, Provider = provider, Status = ContainerStatus.Running }
            });

        var result = await _tools.ListContainers(status: "Running");

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("running1");
        result[0].Status.Should().Be("Running");
    }

    [Fact]
    public async Task GetContainer_ExistingId_ShouldReturnDetail()
    {
        var template = new ContainerTemplate { Code = "full-stack", Name = "Full Stack", Version = "1.0.0", BaseImage = "ubuntu:24.04" };
        var provider = new InfrastructureProvider { Code = "docker-local", Name = "Local Docker", Type = ProviderType.Docker };
        var container = new Container
        {
            Name = "detail-test",
            OwnerId = "user1",
            Template = template,
            Provider = provider,
            Status = ContainerStatus.Running,
            IdeEndpoint = "https://ide.test.com"
        };
        _mockContainerService.Setup(s => s.GetContainerAsync(container.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        var result = await _tools.GetContainer(container.Id.ToString());

        result.Should().NotBeNull();
        result!.Name.Should().Be("detail-test");
        result.IdeEndpoint.Should().Be("https://ide.test.com");
        result.Status.Should().Be("Running");
        result.TemplateName.Should().Be("Full Stack");
        result.ProviderName.Should().Be("Local Docker");
    }

    [Fact]
    public async Task GetContainer_InvalidGuid_ShouldReturnNull()
    {
        var result = await _tools.GetContainer("not-a-guid");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetContainer_NonExistent_ShouldReturnNull()
    {
        _mockContainerService.Setup(s => s.GetContainerAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await _tools.GetContainer(Guid.NewGuid().ToString());

        result.Should().BeNull();
    }

    [Fact]
    public async Task BrowseTemplates_ShouldReturnPublishedOnly()
    {
        _db.Templates.AddRange(
            new ContainerTemplate { Code = "pub", Name = "Published", Version = "1.0", BaseImage = "img", IsPublished = true },
            new ContainerTemplate { Code = "unpub", Name = "Unpublished", Version = "1.0", BaseImage = "img", IsPublished = false }
        );
        await _db.SaveChangesAsync();

        var result = await _tools.BrowseTemplates();

        result.Should().HaveCount(1);
        result[0].Code.Should().Be("pub");
    }

    [Fact]
    public async Task ListProviders_ShouldReturnAll()
    {
        _db.Providers.AddRange(
            new InfrastructureProvider { Code = "docker", Name = "Docker", Type = ProviderType.Docker, Region = "local" },
            new InfrastructureProvider { Code = "apple", Name = "Apple", Type = ProviderType.AppleContainer }
        );
        await _db.SaveChangesAsync();

        var result = await _tools.ListProviders();

        result.Should().HaveCount(2);
        result.Should().Contain(p => p.Code == "docker");
        result.Should().Contain(p => p.Code == "apple");
    }

    [Fact]
    public async Task ListWorkspaces_ShouldReturnAll()
    {
        _db.Workspaces.AddRange(
            new Workspace { Name = "WS1", OwnerId = "user1", GitRepositoryUrl = "https://github.com/test/a" },
            new Workspace { Name = "WS2", OwnerId = "user2" }
        );
        await _db.SaveChangesAsync();

        var result = await _tools.ListWorkspaces();

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListImages_WithExistingTemplate_ShouldReturnImages()
    {
        var template = new ContainerTemplate
        {
            Code = "full-stack",
            Name = "Full Stack",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        _db.Templates.Add(template);
        _db.Images.Add(new ContainerImage
        {
            TemplateId = template.Id,
            ContentHash = "sha256:abc",
            Tag = "full-stack:1.0.0-1",
            ImageReference = "registry/full-stack:1.0.0-1",
            BaseImageDigest = "sha256:base",
            DependencyManifest = "{}",
            DependencyLock = "{}",
            BuildNumber = 1,
            BuildStatus = ImageBuildStatus.Succeeded
        });
        await _db.SaveChangesAsync();

        var result = await _tools.ListImages("full-stack");

        result.Should().HaveCount(1);
        result[0].Tag.Should().Be("full-stack:1.0.0-1");
        result[0].BuildStatus.Should().Be("Succeeded");
    }

    [Fact]
    public async Task ListImages_NonExistentTemplate_ShouldReturnEmpty()
    {
        var result = await _tools.ListImages("nonexistent");

        result.Should().BeEmpty();
    }

    // rivoli-ai/andy-containers#76. Container lifecycle MCP tools.
    // Each test pins one observable behaviour: invalid id short-
    // circuits, ownership gates non-admin users, the tool delegates
    // to IContainerService, the response shape matches the controller.

    private static readonly Guid LifecycleContainerId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static Container LifecycleContainer(string ownerId = "test-user", ContainerStatus status = ContainerStatus.Running)
    {
        var template = new ContainerTemplate
        {
            Code = "full-stack",
            Name = "Full Stack",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
        };
        var provider = new InfrastructureProvider
        {
            Code = "docker-local",
            Name = "Local Docker",
            Type = ProviderType.Docker,
        };
        return new Container
        {
            Id = LifecycleContainerId,
            Name = "lifecycle-test",
            OwnerId = ownerId,
            Template = template,
            Provider = provider,
            Status = status,
        };
    }

    [Fact]
    public async Task StartContainer_HappyPath_ReturnsRefreshedDetail()
    {
        var c = LifecycleContainer(status: ContainerStatus.Stopped);
        _mockContainerService.Setup(s => s.GetContainerAsync(LifecycleContainerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(c);

        var result = await _tools.StartContainer(LifecycleContainerId.ToString());

        result.Should().NotBeNull();
        result!.Id.Should().Be(LifecycleContainerId);
        _mockContainerService.Verify(s => s.StartContainerAsync(LifecycleContainerId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartContainer_InvalidGuid_ReturnsNull_DoesNotCallService()
    {
        var result = await _tools.StartContainer("not-a-guid");

        result.Should().BeNull();
        _mockContainerService.Verify(s => s.StartContainerAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartContainer_KeyNotFound_ReturnsNull()
    {
        _mockContainerService.Setup(s => s.GetContainerAsync(LifecycleContainerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await _tools.StartContainer(LifecycleContainerId.ToString());

        result.Should().BeNull();
    }

    [Fact]
    public async Task StopContainer_HappyPath_ReturnsRefreshedDetail()
    {
        var c = LifecycleContainer(status: ContainerStatus.Running);
        _mockContainerService.Setup(s => s.GetContainerAsync(LifecycleContainerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(c);

        var result = await _tools.StopContainer(LifecycleContainerId.ToString());

        result.Should().NotBeNull();
        result!.Id.Should().Be(LifecycleContainerId);
        _mockContainerService.Verify(s => s.StopContainerAsync(LifecycleContainerId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DestroyContainer_HappyPath_ReturnsConfirmationString()
    {
        var c = LifecycleContainer();
        _mockContainerService.Setup(s => s.GetContainerAsync(LifecycleContainerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(c);

        var result = await _tools.DestroyContainer(LifecycleContainerId.ToString());

        result.Should().Contain("destroyed");
        result.Should().Contain(LifecycleContainerId.ToString());
        _mockContainerService.Verify(s => s.DestroyContainerAsync(LifecycleContainerId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DestroyContainer_InvalidGuid_ReturnsErrorString_DoesNotCallService()
    {
        var result = await _tools.DestroyContainer("not-a-guid");

        result.Should().Contain("Invalid container ID");
        _mockContainerService.Verify(s => s.DestroyContainerAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DestroyContainer_NotFound_ReturnsNotFoundString()
    {
        _mockContainerService.Setup(s => s.GetContainerAsync(LifecycleContainerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await _tools.DestroyContainer(LifecycleContainerId.ToString());

        result.Should().Contain("not found");
    }

    [Fact]
    public async Task ExecCommand_HappyPath_ReturnsExecResult()
    {
        var c = LifecycleContainer();
        _mockContainerService.Setup(s => s.GetContainerAsync(LifecycleContainerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(c);
        _mockContainerService
            .Setup(s => s.ExecAsync(LifecycleContainerId, "ls /tmp", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "hello.txt\n", StdErr = "" });

        var result = await _tools.ExecCommand(LifecycleContainerId.ToString(), "ls /tmp");

        result.Should().NotBeNull();
        result!.ExitCode.Should().Be(0);
        result.StdOut.Should().Be("hello.txt\n");
    }

    [Fact]
    public async Task ExecCommand_BlankCommand_ReturnsNull()
    {
        var result = await _tools.ExecCommand(LifecycleContainerId.ToString(), "   ");

        result.Should().BeNull();
        _mockContainerService.Verify(s => s.ExecAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecCommand_InvalidGuid_ReturnsNull()
    {
        var result = await _tools.ExecCommand("not-a-guid", "ls");

        result.Should().BeNull();
    }
}
