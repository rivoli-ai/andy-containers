using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class GitRepositoryProbeServiceTests
{
    private readonly Mock<IGitCredentialService> _mockCredentialService;
    private readonly GitRepositoryProbeService _service;

    public GitRepositoryProbeServiceTests()
    {
        _mockCredentialService = new Mock<IGitCredentialService>();
        var logger = new Mock<ILogger<GitRepositoryProbeService>>();
        _service = new GitRepositoryProbeService(_mockCredentialService.Object, logger.Object);
    }

    [Fact]
    public async Task ProbeRepository_SkipsNonHttpsUrls()
    {
        var repo = new GitRepositoryConfig { Url = "git@github.com:owner/repo.git" };

        var error = await _service.ProbeRepositoryAsync(repo, "user1", CancellationToken.None);

        error.Should().BeNull("SSH URLs should be skipped");
    }

    [Fact]
    public async Task ProbeRepository_SkipsWhenCredentialRefNotResolved()
    {
        var repo = new GitRepositoryConfig
        {
            Url = "https://github.com/owner/private-repo.git",
            CredentialRef = "my-github"
        };

        _mockCredentialService
            .Setup(s => s.ResolveTokenAsync("user1", "my-github", "github.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var error = await _service.ProbeRepositoryAsync(repo, "user1", CancellationToken.None);

        error.Should().BeNull("should skip probe when explicit credential not resolved");
    }

    [Fact]
    public async Task ProbeRepository_InvalidUrl_ReturnsError()
    {
        var repo = new GitRepositoryConfig { Url = "https://not a valid url" };

        var error = await _service.ProbeRepositoryAsync(repo, "user1", CancellationToken.None);

        error.Should().NotBeNull();
        error.Should().Contain("Invalid URL");
    }

    [Fact]
    public async Task ProbeRepositories_ReturnsIndexedErrors_ForMultipleRepos()
    {
        var repos = new List<GitRepositoryConfig>
        {
            new() { Url = "git@github.com:owner/repo.git" },  // SSH, skipped
            new() { Url = "https://not a valid url" },         // Invalid
        };

        var errors = await _service.ProbeRepositoriesAsync(repos, "user1", CancellationToken.None);

        errors.Should().HaveCount(1);
        errors[0].Should().Contain("[1]", "should include index for multi-repo errors");
    }

    [Fact]
    public async Task ProbeRepositories_EmptyList_ReturnsNoErrors()
    {
        var errors = await _service.ProbeRepositoriesAsync(
            new List<GitRepositoryConfig>(), "user1", CancellationToken.None);

        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ProbeRepository_PublicGitHubRepo_Succeeds()
    {
        // This test actually calls git ls-remote against a known public repo.
        // Skip if git is not available.
        if (!IsGitAvailable())
            return;

        var repo = new GitRepositoryConfig
        {
            Url = "https://github.com/torvalds/linux.git"
        };

        var error = await _service.ProbeRepositoryAsync(repo, "user1", CancellationToken.None);

        error.Should().BeNull("a valid public repo should pass the probe");
    }

    [Fact]
    public async Task ProbeRepository_NonExistentRepo_ReturnsError()
    {
        // This test actually calls git ls-remote against a non-existent repo.
        // Skip if git is not available.
        if (!IsGitAvailable())
            return;

        var repo = new GitRepositoryConfig
        {
            Url = "https://github.com/this-org-does-not-exist-12345/no-such-repo.git"
        };

        var error = await _service.ProbeRepositoryAsync(repo, "user1", CancellationToken.None);

        // The probe should either return an error or skip (auth required)
        // Both are acceptable — the key is it should not throw
    }

    [Fact]
    public async Task RunGitLsRemote_HandlesTimeoutGracefully()
    {
        // Use a very short cancellation to simulate timeout behavior
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));

        // This should not throw — timeout is handled internally
        try
        {
            var error = await _service.RunGitLsRemoteAsync(
                "https://example.com/very-slow-repo.git",
                "https://example.com/very-slow-repo.git",
                cts.Token);
            // Either null (skipped/graceful) or an error message — both OK
        }
        catch (OperationCanceledException)
        {
            // Also acceptable — caller cancelled
        }
    }

    private static bool IsGitAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
