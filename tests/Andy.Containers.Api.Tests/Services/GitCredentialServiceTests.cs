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
}
