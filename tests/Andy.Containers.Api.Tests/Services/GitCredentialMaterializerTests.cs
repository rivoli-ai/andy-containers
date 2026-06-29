using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

/// <summary>
/// Guards the run-dispatch credential-materialisation fix (2026-06-29): a
/// <c>sourceControl.github.pat</c> saved AFTER a container was provisioned must
/// still reach the container, so a PR-author run can push instead of failing
/// <c>[PR-VERIFY-002]</c> ("committed locally but never pushed").
/// </summary>
public class GitCredentialMaterializerTests
{
    private static readonly Guid Container = Guid.NewGuid();

    private static (GitCredentialMaterializer sut,
                    Mock<IGitCredentialService> creds,
                    Mock<ISourceControlSecretResolver> secrets,
                    Mock<IContainerService> containers) Build()
    {
        var creds = new Mock<IGitCredentialService>();
        var secrets = new Mock<ISourceControlSecretResolver>();
        var containers = new Mock<IContainerService>();

        creds.Setup(c => c.ListWithDecryptedTokensAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DecryptedGitCredential>());
        containers.Setup(c => c.ExecAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        var sut = new GitCredentialMaterializer(
            creds.Object, secrets.Object, containers.Object,
            NullLogger<GitCredentialMaterializer>.Instance);
        return (sut, creds, secrets, containers);
    }

    [Fact]
    public async Task Injects_settings_PAT_fallback_when_owner_has_no_github_credential()
    {
        var (sut, _, secrets, containers) = Build();
        secrets.Setup(s => s.GetGitHubPatAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token_value");

        string? injected = null;
        containers.Setup(c => c.ExecAsync(Container, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, TimeSpan, CancellationToken>((_, script, _, _) => injected = script)
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        var result = await sut.MaterializeAsync(Container, containerUser: "agent", ownerId: "andy-tasks-api");

        result.Injected.Should().BeTrue();
        result.UsedSettingsPatFallback.Should().BeTrue();
        result.CredentialCount.Should().Be(1);

        injected.Should().NotBeNull();
        // The token must reach the container as BOTH the git credential.helper
        // store (for `git push`) and GH_TOKEN (for `gh pr create`).
        injected!.Should().Contain("ghp_test_token_value");
        injected!.Should().Contain("export GH_TOKEN=");
        injected!.Should().Contain("credential.helper store");
        containers.Verify(c => c.ExecAsync(Container, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Prefers_owner_github_credential_over_the_settings_PAT_fallback()
    {
        var (sut, creds, secrets, containers) = Build();
        creds.Setup(c => c.ListWithDecryptedTokensAsync("real-user", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new DecryptedGitCredential(Guid.NewGuid(), "my-pat", "github.com",
                    GitCredentialType.PersonalAccessToken, "ghp_user_owned")
            });

        var result = await sut.MaterializeAsync(Container, "agent", "real-user");

        result.Injected.Should().BeTrue();
        result.UsedSettingsPatFallback.Should().BeFalse("the owner already has a github.com credential");
        // The settings PAT must not even be consulted when the owner has one.
        secrets.Verify(s => s.GetGitHubPatAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task No_injection_when_no_owner_credential_and_no_settings_PAT()
    {
        var (sut, _, secrets, containers) = Build();
        secrets.Setup(s => s.GetGitHubPatAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var result = await sut.MaterializeAsync(Container, "agent", "andy-tasks-api");

        result.Injected.Should().BeFalse();
        result.CredentialCount.Should().Be(0);
        // Nothing to inject ⇒ no exec into the container.
        containers.Verify(c => c.ExecAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_failed_secret_resolve_does_not_throw()
    {
        var (sut, _, secrets, _) = Build();
        secrets.Setup(s => s.GetGitHubPatAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("andy-settings unreachable"));

        var act = async () => await sut.MaterializeAsync(Container, "agent", "andy-tasks-api");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task No_container_user_is_a_no_op()
    {
        var (sut, _, _, containers) = Build();

        var result = await sut.MaterializeAsync(Container, containerUser: null, ownerId: "andy-tasks-api");

        result.Injected.Should().BeFalse();
        containers.Verify(c => c.ExecAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
