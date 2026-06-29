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

        var scripts = new List<string>();
        containers.Setup(c => c.ExecAsync(Container, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, TimeSpan, CancellationToken>((_, script, _, _) => scripts.Add(script))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        var result = await sut.MaterializeAsync(Container, containerUser: "agent", ownerId: "andy-tasks-api");

        result.Injected.Should().BeTrue();
        result.UsedSettingsPatFallback.Should().BeTrue();
        result.CredentialCount.Should().Be(1);

        // The interactive (containerUser/login-shell) injection: git credential
        // store + GH_TOKEN export in ~/.bashrc.
        var login = scripts.FirstOrDefault(s => s.Contains("export GH_TOKEN="));
        login.Should().NotBeNull();
        login!.Should().Contain("ghp_test_token_value");
        login!.Should().Contain("credential.helper store");
    }

    [Fact]
    public async Task Also_delivers_env_independent_gh_and_git_auth_to_the_exec_user()
    {
        var (sut, _, secrets, containers) = Build();
        secrets.Setup(s => s.GetGitHubPatAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_test_token_value");

        var scripts = new List<string>();
        containers.Setup(c => c.ExecAsync(Container, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, TimeSpan, CancellationToken>((_, s, _, _) => scripts.Add(s))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        await sut.MaterializeAsync(Container, "agent", "andy-tasks-api");

        // A second exec must deliver the credential the way a NON-login `sh -c`
        // (the agent + verifier) actually reads it: gh's config file + git's
        // credential store, NOT a ~/.bashrc GH_TOKEN export.
        var envIndependent = scripts.FirstOrDefault(s => s.Contains(".config/gh/hosts.yml"));
        envIndependent.Should().NotBeNull("gh reads ~/.config/gh/hosts.yml regardless of shell/env");
        envIndependent!.Should().Contain("oauth_token:");
        envIndependent!.Should().Contain("ghp_test_token_value");
        envIndependent!.Should().Contain("credential.helper store");
        envIndependent!.Should().Contain(".git-credentials");
    }

    [Fact]
    public void ExecUser_auth_script_is_env_independent_and_safely_quoted()
    {
        var script = GitCredentialMaterializer.BuildExecUserGitHubAuthScript("tok'with'quote");
        // No reliance on a login shell / GH_TOKEN export.
        script.Should().Contain("hosts.yml");
        script.Should().Contain("credential.helper store");
        script.Should().NotContain("bashrc");
        // The single-quote in the token is POSIX-escaped, not left to break the shell.
        script.Should().Contain("'\\''");
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
