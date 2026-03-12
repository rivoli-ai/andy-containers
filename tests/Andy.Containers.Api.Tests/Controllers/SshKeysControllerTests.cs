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

public class SshKeysControllerTests : IDisposable
{
    private readonly Mock<ISshKeyService> _mockSshKeyService;
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly ContainersDbContext _db;
    private readonly SshKeysController _controller;

    public SshKeysControllerTests()
    {
        _mockSshKeyService = new Mock<ISshKeyService>();
        _mockCurrentUser = new Mock<ICurrentUserService>();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _mockCurrentUser.Setup(u => u.IsAuthenticated()).Returns(true);
        _db = InMemoryDbHelper.CreateContext();
        _controller = new SshKeysController(_mockSshKeyService.Object, _mockCurrentUser.Object, _db);
    }

    public void Dispose() { _db.Dispose(); }

    [Fact]
    public async Task List_ShouldReturnOkWithKeys()
    {
        var keys = new List<UserSshKey>
        {
            new() { UserId = "test-user", Label = "laptop", PublicKey = "ssh-ed25519 AAAA", Fingerprint = "SHA256:abc", KeyType = "ed25519" },
            new() { UserId = "test-user", Label = "desktop", PublicKey = "ssh-rsa BBBB", Fingerprint = "SHA256:def", KeyType = "rsa" }
        };
        _mockSshKeyService.Setup(s => s.ListKeysAsync("test-user", It.IsAny<CancellationToken>())).ReturnsAsync(keys);

        var result = await _controller.List(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task List_NoKeys_ShouldReturnEmptyList()
    {
        _mockSshKeyService.Setup(s => s.ListKeysAsync("test-user", It.IsAny<CancellationToken>())).ReturnsAsync(new List<UserSshKey>());

        var result = await _controller.List(CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Add_ValidKey_ShouldReturnCreated()
    {
        var request = new AddSshKeyRequest { Label = "my-key", PublicKey = "ssh-ed25519 AAAA test@host" };
        var key = new UserSshKey { UserId = "test-user", Label = "my-key", PublicKey = "ssh-ed25519 AAAA test@host", Fingerprint = "SHA256:abc", KeyType = "ed25519" };
        _mockSshKeyService.Setup(s => s.IsValidPublicKey(request.PublicKey)).Returns(true);
        _mockSshKeyService.Setup(s => s.AddKeyAsync("test-user", "my-key", request.PublicKey, It.IsAny<CancellationToken>())).ReturnsAsync(key);

        var result = await _controller.Add(request, CancellationToken.None);

        result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Add_EmptyLabel_ShouldReturnBadRequest()
    {
        var request = new AddSshKeyRequest { Label = "", PublicKey = "ssh-ed25519 AAAA" };

        var result = await _controller.Add(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Add_EmptyPublicKey_ShouldReturnBadRequest()
    {
        var request = new AddSshKeyRequest { Label = "my-key", PublicKey = "" };

        var result = await _controller.Add(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Add_PrivateKey_ShouldReturnBadRequest()
    {
        var request = new AddSshKeyRequest { Label = "my-key", PublicKey = "-----BEGIN OPENSSH PRIVATE KEY-----\ndata\n-----END OPENSSH PRIVATE KEY-----" };

        var result = await _controller.Add(request, CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Add_InvalidKeyFormat_ShouldReturnUnprocessableEntity()
    {
        var request = new AddSshKeyRequest { Label = "my-key", PublicKey = "not-a-valid-key" };
        _mockSshKeyService.Setup(s => s.IsValidPublicKey("not-a-valid-key")).Returns(false);

        var result = await _controller.Add(request, CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    [Fact]
    public async Task Add_DuplicateKey_ShouldReturnConflict()
    {
        var request = new AddSshKeyRequest { Label = "my-key", PublicKey = "ssh-ed25519 AAAA test@host" };
        _mockSshKeyService.Setup(s => s.IsValidPublicKey(request.PublicKey)).Returns(true);
        _mockSshKeyService.Setup(s => s.AddKeyAsync("test-user", "my-key", request.PublicKey, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SSH key with this fingerprint already exists"));

        var result = await _controller.Add(request, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Remove_ExistingKey_ShouldReturnNoContent()
    {
        var keyId = Guid.NewGuid();
        _mockSshKeyService.Setup(s => s.RemoveKeyAsync("test-user", keyId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _controller.Remove(keyId, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Remove_NonExistentKey_ShouldReturnNotFound()
    {
        var keyId = Guid.NewGuid();
        _mockSshKeyService.Setup(s => s.RemoveKeyAsync("test-user", keyId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _controller.Remove(keyId, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetConfigSnippet_ExistingContainer_SshEnabled_ShouldReturnSnippet()
    {
        var template = new ContainerTemplate { Code = "fs", Name = "Full Stack", Version = "1.0", BaseImage = "ubuntu:24.04" };
        var provider = new InfrastructureProvider { Code = "docker", Name = "Docker", Type = ProviderType.Docker };
        _db.Templates.Add(template);
        _db.Providers.Add(provider);
        var container = new Container
        {
            Name = "my-container",
            OwnerId = "test-user",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            SshEnabled = true,
            IdeEndpoint = "https://container.example.com:8080"
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var result = await _controller.GetConfigSnippet(container.Id, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task GetConfigSnippet_NonExistentContainer_ShouldReturnNotFound()
    {
        var result = await _controller.GetConfigSnippet(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetConfigSnippet_SshDisabled_ShouldReturnBadRequest()
    {
        var template = new ContainerTemplate { Code = "fs", Name = "Full Stack", Version = "1.0", BaseImage = "ubuntu:24.04" };
        var provider = new InfrastructureProvider { Code = "docker", Name = "Docker", Type = ProviderType.Docker };
        _db.Templates.Add(template);
        _db.Providers.Add(provider);
        var container = new Container
        {
            Name = "no-ssh",
            OwnerId = "test-user",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            SshEnabled = false
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var result = await _controller.GetConfigSnippet(container.Id, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
