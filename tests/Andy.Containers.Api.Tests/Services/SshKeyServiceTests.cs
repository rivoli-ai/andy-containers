using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class SshKeyServiceTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly SshKeyService _service;

    private const string ValidEd25519Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl user@host";
    private const string ValidRsaKey = "ssh-rsa ZmFrZS1yc2Eta2V5LWRhdGEtcGxhY2Vob2xkZXItMTIzNDU2Nzg5MGFiY2RlZg== user@host";
    private const string ValidEcdsaKey = "ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBCExyz0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnop== user@host";

    public SshKeyServiceTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _service = new SshKeyService(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AddKeyAsync_ValidEd25519Key_StoresWithFingerprint()
    {
        var key = await _service.AddKeyAsync("user1", "My Laptop", ValidEd25519Key);

        key.UserId.Should().Be("user1");
        key.Label.Should().Be("My Laptop");
        key.KeyType.Should().Be("ed25519");
        key.Fingerprint.Should().StartWith("SHA256:");
        key.Id.Should().NotBeEmpty();
    }

    [Fact]
    public async Task AddKeyAsync_ValidRsaKey_Accepted()
    {
        var key = await _service.AddKeyAsync("user1", "RSA Key", ValidRsaKey);

        key.KeyType.Should().Be("rsa");
        key.Fingerprint.Should().StartWith("SHA256:");
    }

    [Fact]
    public async Task AddKeyAsync_ValidEcdsaKey_Accepted()
    {
        var key = await _service.AddKeyAsync("user1", "ECDSA Key", ValidEcdsaKey);

        key.KeyType.Should().Be("ecdsa");
    }

    [Fact]
    public async Task AddKeyAsync_InvalidFormat_ThrowsArgumentException()
    {
        var act = () => _service.AddKeyAsync("user1", "Bad Key", "not-a-valid-key");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Invalid SSH public key*");
    }

    [Fact]
    public async Task AddKeyAsync_PrivateKey_ThrowsArgumentException()
    {
        var privateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjE=\n-----END OPENSSH PRIVATE KEY-----";

        var act = () => _service.AddKeyAsync("user1", "Private", privateKey);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Invalid SSH public key*");
    }

    [Fact]
    public async Task AddKeyAsync_DuplicateFingerprint_ThrowsInvalidOperation()
    {
        await _service.AddKeyAsync("user1", "Key 1", ValidEd25519Key);

        var act = () => _service.AddKeyAsync("user1", "Key 2", ValidEd25519Key);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*fingerprint already exists*");
    }

    [Fact]
    public async Task AddKeyAsync_ExceedsMaxKeys_ThrowsInvalidOperation()
    {
        // Add 20 keys (max)
        for (int i = 0; i < 20; i++)
        {
            _db.SshKeys.Add(new UserSshKey
            {
                UserId = "user1",
                Label = $"Key {i}",
                PublicKey = $"ssh-ed25519 AAAA{i:D4}placeholder key{i}",
                Fingerprint = $"SHA256:fingerprint{i}",
                KeyType = "ed25519"
            });
        }
        await _db.SaveChangesAsync();

        var act = () => _service.AddKeyAsync("user1", "Key 21", ValidEd25519Key);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Maximum*");
    }

    [Fact]
    public async Task ListKeysAsync_ReturnsOnlyUserKeys()
    {
        await _service.AddKeyAsync("user1", "User1 Key", ValidEd25519Key);
        await _service.AddKeyAsync("user2", "User2 Key", ValidRsaKey);

        var keys = await _service.ListKeysAsync("user1");

        keys.Should().HaveCount(1);
        keys[0].Label.Should().Be("User1 Key");
    }

    [Fact]
    public async Task RemoveKeyAsync_ExistingKey_ReturnsTrue()
    {
        var key = await _service.AddKeyAsync("user1", "To Remove", ValidEd25519Key);

        var removed = await _service.RemoveKeyAsync("user1", key.Id);

        removed.Should().BeTrue();
        (await _service.ListKeysAsync("user1")).Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveKeyAsync_NonExistentKey_ReturnsFalse()
    {
        var removed = await _service.RemoveKeyAsync("user1", Guid.NewGuid());

        removed.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveKeyAsync_OtherUsersKey_ReturnsFalse()
    {
        var key = await _service.AddKeyAsync("user1", "User1 Key", ValidEd25519Key);

        var removed = await _service.RemoveKeyAsync("user2", key.Id);

        removed.Should().BeFalse();
    }

    [Theory]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5 user@host")]
    [InlineData("ssh-rsa AAAAB3NzaC1yc2E user@host")]
    [InlineData("ecdsa-sha2-nistp256 AAAAE2VjZHNh user@host")]
    public void IsValidPublicKey_ValidKeys_ReturnsTrue(string key)
    {
        _service.IsValidPublicKey(key).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-key")]
    [InlineData("ssh-dsa AAAA user@host")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
    public void IsValidPublicKey_InvalidKeys_ReturnsFalse(string key)
    {
        _service.IsValidPublicKey(key).Should().BeFalse();
    }

    [Theory]
    [InlineData("ssh-rsa AAAA user", "rsa")]
    [InlineData("ssh-ed25519 AAAA user", "ed25519")]
    [InlineData("ecdsa-sha2-nistp256 AAAA user", "ecdsa")]
    public void DetectKeyType_CorrectlyIdentifiesType(string key, string expectedType)
    {
        _service.DetectKeyType(key).Should().Be(expectedType);
    }

    [Fact]
    public void ComputeFingerprint_ReturnsConsistentSha256()
    {
        var fp1 = _service.ComputeFingerprint(ValidEd25519Key);
        var fp2 = _service.ComputeFingerprint(ValidEd25519Key);

        fp1.Should().StartWith("SHA256:");
        fp1.Should().Be(fp2);
    }

    // === Story 11: UpdateLastUsedAsync ===

    [Fact]
    public async Task UpdateLastUsedAsync_UpdatesLastUsedAtOnSpecifiedKeys()
    {
        var key1 = await _service.AddKeyAsync("user1", "Key 1", ValidEd25519Key);
        var key2 = await _service.AddKeyAsync("user1", "Key 2", ValidRsaKey);

        await _service.UpdateLastUsedAsync("user1", [key1.Id, key2.Id]);

        var keys = await _service.ListKeysAsync("user1");
        keys.Should().AllSatisfy(k => k.LastUsedAt.Should().NotBeNull());
    }

    [Fact]
    public async Task UpdateLastUsedAsync_IgnoresKeysBelongingToOtherUser()
    {
        var key = await _service.AddKeyAsync("user1", "Key 1", ValidEd25519Key);

        await _service.UpdateLastUsedAsync("user2", [key.Id]);

        var keys = await _service.ListKeysAsync("user1");
        keys[0].LastUsedAt.Should().BeNull();
    }

    [Fact]
    public async Task UpdateLastUsedAsync_EmptyList_IsNoOp()
    {
        await _service.AddKeyAsync("user1", "Key 1", ValidEd25519Key);

        await _service.UpdateLastUsedAsync("user1", []);

        var keys = await _service.ListKeysAsync("user1");
        keys[0].LastUsedAt.Should().BeNull();
    }
}
