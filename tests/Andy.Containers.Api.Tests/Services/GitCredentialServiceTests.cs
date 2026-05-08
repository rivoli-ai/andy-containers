using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class GitCredentialServiceTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly IGitCredentialService _service;

    public GitCredentialServiceTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        var dataProtectionProvider = DataProtectionProvider.Create("Tests");
        _service = new GitCredentialService(_db, dataProtectionProvider);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task Create_ShouldEncryptToken()
    {
        var credential = await _service.CreateAsync("user1", "my-github", "ghp_secrettoken123", "github.com");

        credential.Id.Should().NotBeEmpty();
        credential.OwnerId.Should().Be("user1");
        credential.Label.Should().Be("my-github");
        credential.GitHost.Should().Be("github.com");
        credential.EncryptedToken.Should().NotBe("ghp_secrettoken123");
        credential.EncryptedToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ResolveToken_ByLabel_ShouldDecryptCorrectly()
    {
        await _service.CreateAsync("user1", "my-github", "ghp_secrettoken123", "github.com");

        var token = await _service.ResolveTokenAsync("user1", "my-github");

        token.Should().Be("ghp_secrettoken123");
    }

    [Fact]
    public async Task ResolveToken_ByHost_ShouldAutoMatch()
    {
        await _service.CreateAsync("user1", "work-github", "ghp_worktoken", "github.com");

        var token = await _service.ResolveTokenAsync("user1", null, "github.com");

        token.Should().Be("ghp_worktoken");
    }

    [Fact]
    public async Task ResolveToken_LabelMatchTakesPrecedence()
    {
        await _service.CreateAsync("user1", "work-github", "ghp_worktoken", "github.com");
        await _service.CreateAsync("user1", "personal-github", "ghp_personaltoken", "github.com");

        var token = await _service.ResolveTokenAsync("user1", "personal-github", "github.com");

        token.Should().Be("ghp_personaltoken");
    }

    [Fact]
    public async Task ResolveToken_WrongOwner_ShouldReturnNull()
    {
        await _service.CreateAsync("user1", "my-github", "ghp_secrettoken123", "github.com");

        var token = await _service.ResolveTokenAsync("user2", "my-github");

        token.Should().BeNull();
    }

    [Fact]
    public async Task ResolveToken_NoMatch_ShouldReturnNull()
    {
        var token = await _service.ResolveTokenAsync("user1", "nonexistent");

        token.Should().BeNull();
    }

    [Fact]
    public async Task ResolveToken_ShouldUpdateLastUsedAt()
    {
        var credential = await _service.CreateAsync("user1", "my-github", "ghp_token");
        credential.LastUsedAt.Should().BeNull();

        await _service.ResolveTokenAsync("user1", "my-github");

        var updated = await _db.GitCredentials.FindAsync(credential.Id);
        updated!.LastUsedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task List_ShouldReturnOwnerCredentials()
    {
        await _service.CreateAsync("user1", "github", "token1");
        await _service.CreateAsync("user1", "gitlab", "token2");
        await _service.CreateAsync("user2", "other", "token3");

        var credentials = await _service.ListAsync("user1");

        credentials.Should().HaveCount(2);
    }

    [Fact]
    public async Task Delete_ExistingCredential_ShouldRemove()
    {
        var credential = await _service.CreateAsync("user1", "my-github", "ghp_token");

        var deleted = await _service.DeleteAsync(credential.Id, "user1");

        deleted.Should().BeTrue();
        var remaining = await _service.ListAsync("user1");
        remaining.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_WrongOwner_ShouldReturnFalse()
    {
        var credential = await _service.CreateAsync("user1", "my-github", "ghp_token");

        var deleted = await _service.DeleteAsync(credential.Id, "user2");

        deleted.Should().BeFalse();
    }

    // ---- #1046 ListWithDecryptedTokensAsync ----

    [Fact]
    public async Task ListWithDecryptedTokens_ReturnsAllOwnerCredentialsDecrypted()
    {
        await _service.CreateAsync("user1", "github-pat", "ghp_a", "github.com", GitCredentialType.PersonalAccessToken);
        await _service.CreateAsync("user1", "deploy-key", "-----BEGIN RSA PRIVATE KEY-----\nABC\n-----END RSA PRIVATE KEY-----", "git.internal", GitCredentialType.DeployKey);
        // Different owner — must not appear in user1's result.
        await _service.CreateAsync("user2", "other", "tok", "github.com");

        var decrypted = await _service.ListWithDecryptedTokensAsync("user1");

        decrypted.Should().HaveCount(2);
        decrypted.Should().Contain(c => c.Label == "github-pat" && c.PlaintextToken == "ghp_a"
                                      && c.CredentialType == GitCredentialType.PersonalAccessToken);
        decrypted.Should().Contain(c => c.Label == "deploy-key" && c.PlaintextToken.Contains("BEGIN RSA")
                                      && c.CredentialType == GitCredentialType.DeployKey);
    }

    [Fact]
    public async Task ListWithDecryptedTokens_NoCredentials_ReturnsEmpty()
    {
        var decrypted = await _service.ListWithDecryptedTokensAsync("user1");
        decrypted.Should().BeEmpty();
    }

    [Fact]
    public async Task ListWithDecryptedTokens_DecryptionFailure_SkipsTheRow()
    {
        // Create a row with a deliberately corrupted EncryptedToken so
        // decryption throws. The contract: skip the bad row, keep
        // returning the rest. Container provisioning shouldn't fail
        // because one credential got into a weird state.
        await _service.CreateAsync("user1", "good", "ok", "github.com");
        var corruptRow = new GitCredential
        {
            OwnerId = "user1",
            Label = "corrupt",
            GitHost = "gitlab.com",
            CredentialType = GitCredentialType.PersonalAccessToken,
            EncryptedToken = "this-is-not-protected-bytes",
        };
        _db.GitCredentials.Add(corruptRow);
        await _db.SaveChangesAsync();

        var decrypted = await _service.ListWithDecryptedTokensAsync("user1");

        decrypted.Should().HaveCount(1);
        decrypted[0].Label.Should().Be("good");
    }
}
