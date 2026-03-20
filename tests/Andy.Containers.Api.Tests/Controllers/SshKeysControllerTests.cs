using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

public class SshKeysControllerTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly ISshKeyService _sshKeyService;
    private readonly Mock<ISshProvisioningService> _mockProvisioning;
    private readonly Mock<IContainerPermissionService> _mockPermissions;
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

        _sshKeyService = new SshKeyService(_db, new NullLogger<SshKeyService>());
        _mockProvisioning = new Mock<ISshProvisioningService>();
        _mockPermissions = new Mock<IContainerPermissionService>();
        _mockPermissions.Setup(p => p.HasPermissionAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _controller = new SshKeysController(_sshKeyService, _mockProvisioning.Object, _mockCurrentUser.Object, _mockPermissions.Object, _db);
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

    // === Story 11: LastUsedAt tracking ===

    [Fact]
    public async Task InjectKey_MatchingRegisteredKey_UpdatesLastUsedAt()
    {
        var container = new Container { Name = "ssh-test", OwnerId = TestUserId, SshEnabled = true };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        // Register the key first so it exists in DB
        var key = await _sshKeyService.AddKeyAsync(TestUserId, "Laptop", ValidEd25519Key);
        key.LastUsedAt.Should().BeNull();

        var request = new InjectSshKeyRequest { PublicKey = ValidEd25519Key };
        var result = await _controller.InjectKey(container.Id, request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();

        // Verify LastUsedAt was updated
        var keys = await _sshKeyService.ListKeysAsync(TestUserId);
        keys[0].LastUsedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task List_ReturnsLastUsedAtTimestamp()
    {
        var key = await _sshKeyService.AddKeyAsync(TestUserId, "Laptop", ValidEd25519Key);

        // Update LastUsedAt
        await _sshKeyService.UpdateLastUsedAsync(TestUserId, [key.Id]);

        var result = await _controller.List(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var keys = ok.Value.Should().BeAssignableTo<IEnumerable<SshKeyDto>>().Subject.ToList();
        keys.Should().HaveCount(1);
        keys[0].LastUsedAt.Should().NotBeNull();
    }

    // === Story 17: container:connect permission ===

    [Fact]
    public async Task InjectKey_NoPermission_Returns403()
    {
        var container = new Container { Name = "denied", OwnerId = "other-user", SshEnabled = true };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        _mockPermissions.Setup(p => p.HasPermissionAsync(TestUserId, container.Id, "container:connect", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var request = new InjectSshKeyRequest { PublicKey = ValidEd25519Key };
        var result = await _controller.InjectKey(container.Id, request, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task GetSshConfig_NoPermission_Returns403()
    {
        var container = new Container { Name = "denied-config", OwnerId = "other-user", SshEnabled = true };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        _mockPermissions.Setup(p => p.HasPermissionAsync(TestUserId, container.Id, "container:connect", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.GetSshConfig(container.Id, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    // === Story 18: Audit logging ===

    [Fact]
    public async Task InjectKey_CreatesContainerEvent_SshKeyInjected()
    {
        var container = new Container { Name = "audit-inject", OwnerId = TestUserId, SshEnabled = true };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var request = new InjectSshKeyRequest { PublicKey = ValidEd25519Key };
        var result = await _controller.InjectKey(container.Id, request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();

        var events = await _db.Events.Where(e => e.ContainerId == container.Id).ToListAsync();
        events.Should().ContainSingle(e => e.EventType == ContainerEventType.SshKeyInjected);
        var evt = events.First(e => e.EventType == ContainerEventType.SshKeyInjected);
        evt.SubjectId.Should().Be(TestUserId);
        evt.Details.Should().Contain("fingerprint");
    }
}
