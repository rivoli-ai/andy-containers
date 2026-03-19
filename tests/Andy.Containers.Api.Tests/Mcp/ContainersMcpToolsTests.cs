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
    private readonly Mock<ISshKeyService> _mockSshKeyService;
    private readonly Mock<ISshProvisioningService> _mockSshProvisioning;
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly ContainersMcpTools _tools;

    public ContainersMcpToolsTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockSshKeyService = new Mock<ISshKeyService>();
        _mockSshProvisioning = new Mock<ISshProvisioningService>();
        _mockCurrentUser = new Mock<ICurrentUserService>();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _tools = new ContainersMcpTools(_db, _mockSshKeyService.Object, _mockSshProvisioning.Object, _mockCurrentUser.Object);
    }

    public void Dispose() => _db.Dispose();

    private async Task<(ContainerTemplate template, InfrastructureProvider provider)> SeedTemplateAndProvider()
    {
        var template = new ContainerTemplate
        {
            Code = "full-stack",
            Name = "Full Stack",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            IsPublished = true
        };
        var provider = new InfrastructureProvider
        {
            Code = "docker-local",
            Name = "Local Docker",
            Type = ProviderType.Docker
        };
        _db.Templates.Add(template);
        _db.Providers.Add(provider);
        await _db.SaveChangesAsync();
        return (template, provider);
    }

    // === Original MCP tool tests ===

    [Fact]
    public async Task ListContainers_NoContainers_ShouldReturnEmpty()
    {
        var result = await _tools.ListContainers();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListContainers_WithContainers_ShouldReturnAll()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        _db.Containers.AddRange(
            new Container { Name = "c1", OwnerId = "user1", TemplateId = template.Id, ProviderId = provider.Id, Status = ContainerStatus.Running },
            new Container { Name = "c2", OwnerId = "user1", TemplateId = template.Id, ProviderId = provider.Id, Status = ContainerStatus.Stopped }
        );
        await _db.SaveChangesAsync();

        var result = await _tools.ListContainers();
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListContainers_WithStatusFilter_ShouldFilterCorrectly()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        _db.Containers.AddRange(
            new Container { Name = "running1", OwnerId = "user1", TemplateId = template.Id, ProviderId = provider.Id, Status = ContainerStatus.Running },
            new Container { Name = "stopped1", OwnerId = "user1", TemplateId = template.Id, ProviderId = provider.Id, Status = ContainerStatus.Stopped }
        );
        await _db.SaveChangesAsync();

        var result = await _tools.ListContainers(status: "Running");
        result.Should().HaveCount(1);
        result[0].Name.Should().Be("running1");
    }

    [Fact]
    public async Task GetContainer_ExistingId_ShouldReturnDetail()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = new Container
        {
            Name = "detail-test", OwnerId = "user1", TemplateId = template.Id, ProviderId = provider.Id,
            Status = ContainerStatus.Running, IdeEndpoint = "https://ide.test.com"
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var result = await _tools.GetContainer(container.Id.ToString());
        result.Should().NotBeNull();
        result!.Name.Should().Be("detail-test");
        result.IdeEndpoint.Should().Be("https://ide.test.com");
    }

    [Fact]
    public async Task GetContainer_InvalidGuid_ShouldReturnNull()
    {
        var result = await _tools.GetContainer("not-a-guid");
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
        var template = new ContainerTemplate { Code = "full-stack", Name = "Full Stack", Version = "1.0.0", BaseImage = "ubuntu:24.04" };
        _db.Templates.Add(template);
        _db.Images.Add(new ContainerImage
        {
            TemplateId = template.Id, ContentHash = "sha256:abc", Tag = "full-stack:1.0.0-1",
            ImageReference = "registry/full-stack:1.0.0-1", BaseImageDigest = "sha256:base",
            DependencyManifest = "{}", DependencyLock = "{}", BuildNumber = 1, BuildStatus = ImageBuildStatus.Succeeded
        });
        await _db.SaveChangesAsync();

        var result = await _tools.ListImages("full-stack");
        result.Should().HaveCount(1);
        result[0].Tag.Should().Be("full-stack:1.0.0-1");
    }

    [Fact]
    public async Task ListImages_NonExistentTemplate_ShouldReturnEmpty()
    {
        var result = await _tools.ListImages("nonexistent");
        result.Should().BeEmpty();
    }

    // === SSH MCP tool tests ===

    [Fact]
    public async Task ListSshKeys_ReturnsCurrentUserKeys()
    {
        var keys = new List<UserSshKey>
        {
            new() { UserId = "test-user", Label = "Laptop", PublicKey = "ssh-ed25519 AAAA", Fingerprint = "SHA256:abc", KeyType = "ed25519" },
            new() { UserId = "test-user", Label = "CI", PublicKey = "ssh-rsa AAAA", Fingerprint = "SHA256:def", KeyType = "rsa" }
        };
        _mockSshKeyService.Setup(s => s.ListKeysAsync("test-user", default))
            .ReturnsAsync(keys);

        var result = await _tools.ListSshKeys();

        result.Should().HaveCount(2);
        result[0].Label.Should().Be("Laptop");
        result[1].Label.Should().Be("CI");
    }

    [Fact]
    public async Task AddSshKey_ValidKey_ReturnsKeyInfo()
    {
        var key = new UserSshKey
        {
            UserId = "test-user", Label = "New Key", PublicKey = "ssh-ed25519 AAAA",
            Fingerprint = "SHA256:newkey", KeyType = "ed25519"
        };
        _mockSshKeyService.Setup(s => s.AddKeyAsync("test-user", "New Key", "ssh-ed25519 AAAA", default))
            .ReturnsAsync(key);

        var result = await _tools.AddSshKey("New Key", "ssh-ed25519 AAAA");

        result.Label.Should().Be("New Key");
        result.Fingerprint.Should().Be("SHA256:newkey");
    }

    [Fact]
    public async Task AddSshKey_InvalidKey_ThrowsFromService()
    {
        _mockSshKeyService.Setup(s => s.AddKeyAsync("test-user", "Bad", "invalid", default))
            .ThrowsAsync(new ArgumentException("Invalid SSH public key format"));

        var act = () => _tools.AddSshKey("Bad", "invalid");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RemoveSshKey_ExistingKey_ReturnsSuccess()
    {
        var keyId = Guid.NewGuid();
        _mockSshKeyService.Setup(s => s.RemoveKeyAsync("test-user", keyId, default))
            .ReturnsAsync(true);

        var result = await _tools.RemoveSshKey(keyId.ToString());

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveSshKey_NonExistent_ReturnsFailure()
    {
        var keyId = Guid.NewGuid();
        _mockSshKeyService.Setup(s => s.RemoveKeyAsync("test-user", keyId, default))
            .ReturnsAsync(false);

        var result = await _tools.RemoveSshKey(keyId.ToString());

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task GetSshConnectionInfo_SshEnabled_ReturnsDetails()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = new Container
        {
            Name = "ssh-container", OwnerId = "test-user", TemplateId = template.Id,
            ProviderId = provider.Id, SshEnabled = true
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var result = await _tools.GetSshConnectionInfo(container.Id.ToString());

        result.Should().NotBeNull();
        result!.SshEnabled.Should().BeTrue();
        result.Username.Should().Be("dev");
        result.ConfigSnippet.Should().Contain("Host andy-container-");
    }

    [Fact]
    public async Task GetSshConnectionInfo_SshNotEnabled_ReturnsSshDisabledMessage()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = new Container
        {
            Name = "no-ssh", OwnerId = "test-user", TemplateId = template.Id,
            ProviderId = provider.Id, SshEnabled = false
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var result = await _tools.GetSshConnectionInfo(container.Id.ToString());

        result.Should().NotBeNull();
        result!.SshEnabled.Should().BeFalse();
        result.ConfigSnippet.Should().Contain("not enabled");
    }

    [Fact]
    public async Task GetSshConnectionInfo_NonExistent_ReturnsNull()
    {
        var result = await _tools.GetSshConnectionInfo(Guid.NewGuid().ToString());
        result.Should().BeNull();
    }

    [Fact]
    public async Task AddSshKeyToContainer_SshEnabled_ReturnsSuccess()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = new Container
        {
            Name = "inject-test", OwnerId = "test-user", TemplateId = template.Id,
            ProviderId = provider.Id, SshEnabled = true
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        _mockSshKeyService.Setup(s => s.IsValidPublicKey("ssh-ed25519 AAAA")).Returns(true);

        var result = await _tools.AddSshKeyToContainer(container.Id.ToString(), "ssh-ed25519 AAAA");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task AddSshKeyToContainer_SshNotEnabled_ReturnsFailure()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = new Container
        {
            Name = "no-ssh-inject", OwnerId = "test-user", TemplateId = template.Id,
            ProviderId = provider.Id, SshEnabled = false
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var result = await _tools.AddSshKeyToContainer(container.Id.ToString(), "ssh-ed25519 AAAA");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("not enabled");
    }

    [Fact]
    public async Task AddSshKeyToContainer_NonExistent_ReturnsFailure()
    {
        var result = await _tools.AddSshKeyToContainer(Guid.NewGuid().ToString(), "ssh-ed25519 AAAA");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("not found");
    }
}
