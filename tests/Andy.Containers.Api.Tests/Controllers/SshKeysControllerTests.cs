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
    private readonly ContainersDbContext _db;
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly ISshKeyService _sshKeyService;
    private readonly Mock<ISshProvisioningService> _mockProvisioning;
    private readonly SshKeysController _controller;

    private const string TestUserId = "test-user";
    private const string ValidEd25519Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl user@host";
    private const string ValidRsaKey = "ssh-rsa ZmFrZS1yc2Eta2V5LWRhdGEtcGxhY2Vob2xkZXItMTIzNDU2Nzg5MGFiY2RlZg== user@host";

    public SshKeysControllerTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockCurrentUser = new Mock<ICurrentUserService>();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns(TestUserId);
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        _mockCurrentUser.Setup(u => u.IsAuthenticated()).Returns(true);

        _sshKeyService = new SshKeyService(_db);
        _mockProvisioning = new Mock<ISshProvisioningService>();
        _controller = new SshKeysController(_sshKeyService, _mockProvisioning.Object, _mockCurrentUser.Object, _db);
    }

    public void Dispose() => _db.Dispose();

    // --- POST /api/ssh-keys ---

    [Fact]
    public async Task Register_ValidKey_ReturnsCreatedWithFingerprint()
    {
        var request = new RegisterSshKeyRequest { Label = "My Laptop", PublicKey = ValidEd25519Key };

        var result = await _controller.Register(request, CancellationToken.None);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var dto = created.Value.Should().BeOfType<SshKeyDto>().Subject;
        dto.Label.Should().Be("My Laptop");
        dto.Fingerprint.Should().StartWith("SHA256:");
        dto.KeyType.Should().Be("ed25519");
    }

    [Fact]
    public async Task Register_InvalidKeyFormat_Returns422()
    {
        var request = new RegisterSshKeyRequest { Label = "Bad", PublicKey = "not-a-valid-key" };

        var result = await _controller.Register(request, CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    [Fact]
    public async Task Register_PrivateKey_Returns400()
    {
        var request = new RegisterSshKeyRequest
        {
            Label = "Private",
            PublicKey = "-----BEGIN OPENSSH PRIVATE KEY-----\ndata\n-----END OPENSSH PRIVATE KEY-----"
        };

        var result = await _controller.Register(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Register_ExceedsMaxKeys_Returns422()
    {
        for (int i = 0; i < 20; i++)
        {
            _db.SshKeys.Add(new UserSshKey
            {
                UserId = TestUserId,
                Label = $"Key {i}",
                PublicKey = $"ssh-ed25519 AAAA{i:D4}placeholder key{i}",
                Fingerprint = $"SHA256:fp{i}",
                KeyType = "ed25519"
            });
        }
        await _db.SaveChangesAsync();

        var request = new RegisterSshKeyRequest { Label = "Overflow", PublicKey = ValidEd25519Key };

        var result = await _controller.Register(request, CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    [Fact]
    public async Task Register_DuplicateFingerprint_Returns409()
    {
        await _sshKeyService.AddKeyAsync(TestUserId, "First", ValidEd25519Key);

        var request = new RegisterSshKeyRequest { Label = "Duplicate", PublicKey = ValidEd25519Key };

        var result = await _controller.Register(request, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    // --- GET /api/ssh-keys ---

    [Fact]
    public async Task List_ReturnsOnlyCurrentUserKeys()
    {
        await _sshKeyService.AddKeyAsync(TestUserId, "My Key", ValidEd25519Key);
        await _sshKeyService.AddKeyAsync("other-user", "Other Key", ValidRsaKey);

        var result = await _controller.List(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var keys = ok.Value.Should().BeAssignableTo<IEnumerable<SshKeyDto>>().Subject.ToList();
        keys.Should().HaveCount(1);
        keys[0].Label.Should().Be("My Key");
    }

    [Fact]
    public async Task List_NoKeys_ReturnsEmptyArray()
    {
        var result = await _controller.List(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var keys = ok.Value.Should().BeAssignableTo<IEnumerable<SshKeyDto>>().Subject.ToList();
        keys.Should().BeEmpty();
    }

    // --- DELETE /api/ssh-keys/{id} ---

    [Fact]
    public async Task Remove_ExistingKey_Returns204()
    {
        var key = await _sshKeyService.AddKeyAsync(TestUserId, "To Remove", ValidEd25519Key);

        var result = await _controller.Remove(key.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Remove_NonExistent_Returns404()
    {
        var result = await _controller.Remove(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Remove_OtherUsersKey_Returns404()
    {
        var key = await _sshKeyService.AddKeyAsync("other-user", "Other Key", ValidEd25519Key);

        var result = await _controller.Remove(key.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // --- POST /api/containers/{id}/ssh-keys ---

    [Fact]
    public async Task InjectKey_ValidKey_ReturnsOk()
    {
        var container = new Container { Name = "ssh-test", OwnerId = TestUserId, SshEnabled = true };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var request = new InjectSshKeyRequest { PublicKey = ValidEd25519Key };
        var result = await _controller.InjectKey(container.Id, request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task InjectKey_NonExistentContainer_Returns404()
    {
        var request = new InjectSshKeyRequest { PublicKey = ValidEd25519Key };
        var result = await _controller.InjectKey(Guid.NewGuid(), request, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task InjectKey_SshNotEnabled_Returns400()
    {
        var container = new Container { Name = "no-ssh", OwnerId = TestUserId, SshEnabled = false };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var request = new InjectSshKeyRequest { PublicKey = ValidEd25519Key };
        var result = await _controller.InjectKey(container.Id, request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- GET /api/containers/{id}/ssh-config ---

    [Fact]
    public async Task GetSshConfig_SshEnabled_ReturnsConfigSnippet()
    {
        var container = new Container { Name = "ssh-config-test", OwnerId = TestUserId, SshEnabled = true };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var result = await _controller.GetSshConfig(container.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetSshConfig_SshNotEnabled_Returns400()
    {
        var container = new Container { Name = "no-ssh", OwnerId = TestUserId, SshEnabled = false };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var result = await _controller.GetSshConfig(container.Id, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
