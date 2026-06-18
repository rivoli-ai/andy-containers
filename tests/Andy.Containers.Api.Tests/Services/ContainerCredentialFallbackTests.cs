using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

/// <summary>
/// rivoli-ai/conductor#2242. The provisioning worker injects the andy-settings
/// GitHub PAT (sourceControl.github.pat) as a FALLBACK credential ONLY when the
/// user has registered no github.com credential of their own — so `git push` +
/// `gh pr create` work in the task container. Proves the pure merge decision and
/// the synthesized fallback shape (the exact GOAL-23 gap: no credential in the
/// container). Plus: it builds the gh-authenticating injection script end-to-end.
/// </summary>
public class ContainerCredentialFallbackTests
{
    private static DecryptedGitCredential GitHubPat(string token, string? host = "github.com") =>
        new(Guid.NewGuid(), "github", host, GitCredentialType.PersonalAccessToken, token);

    [Fact]
    public void NoUserCredential_WithSettingsPat_AddsGitHubFallback()
    {
        var merged = ContainerProvisioningWorker.ResolveContainerCredentials(
            Array.Empty<DecryptedGitCredential>(), settingsPat: "ghp_fallback");

        merged.Should().ContainSingle();
        merged[0].GitHost.Should().Be("github.com");
        merged[0].CredentialType.Should().Be(GitCredentialType.PersonalAccessToken);
        merged[0].PlaintextToken.Should().Be("ghp_fallback");
        merged[0].Label.Should().Be("sourceControl.github.pat");
    }

    [Fact]
    public void NoUserCredential_NoSettingsPat_RemainsEmpty()
    {
        // The exact GOAL-23 state: no user credential AND no PAT secret set.
        var merged = ContainerProvisioningWorker.ResolveContainerCredentials(
            Array.Empty<DecryptedGitCredential>(), settingsPat: null);

        merged.Should().BeEmpty("with no credential the injector produces no script and the worker logs the actionable warning");
    }

    [Fact]
    public void UserHasGitHubCredential_DoesNotAddFallback()
    {
        var userCreds = new[] { GitHubPat("ghp_user") };

        var merged = ContainerProvisioningWorker.ResolveContainerCredentials(userCreds, settingsPat: "ghp_settings");

        merged.Should().ContainSingle("the user's own github credential wins; no fallback layered on top");
        merged[0].PlaintextToken.Should().Be("ghp_user");
    }

    [Fact]
    public void UserHasOnlyNonGitHubCredential_StillAddsGitHubFallback()
    {
        // A gitlab credential doesn't cover github.com → the PR fallback is still needed.
        var userCreds = new[]
        {
            new DecryptedGitCredential(Guid.NewGuid(), "gitlab", "gitlab.com",
                GitCredentialType.PersonalAccessToken, "glpat"),
        };

        var merged = ContainerProvisioningWorker.ResolveContainerCredentials(userCreds, settingsPat: "ghp_settings");

        merged.Should().HaveCount(2);
        merged.Should().Contain(c => c.GitHost == "github.com" && c.PlaintextToken == "ghp_settings");
    }

    [Fact]
    public void BuildGitHubFallbackCredential_NullOrEmptyPat_ReturnsNull()
    {
        ContainerProvisioningWorker.BuildGitHubFallbackCredential(null).Should().BeNull();
        ContainerProvisioningWorker.BuildGitHubFallbackCredential("").Should().BeNull();
        ContainerProvisioningWorker.BuildGitHubFallbackCredential("  ").Should().BeNull();
    }

    [Fact]
    public void Fallback_ProducesGhAuthenticatingInjectionScript()
    {
        // End-to-end: the synthesized fallback flows through the injector and
        // yields a script that authenticates BOTH git push (helper store) and
        // gh pr create (GH_TOKEN) — closing the GOAL-23 gap.
        var merged = ContainerProvisioningWorker.ResolveContainerCredentials(
            Array.Empty<DecryptedGitCredential>(), settingsPat: "ghp_fallback");

        var script = GitCredentialInjector.BuildInjectionScript("runner", merged);

        script.Should().NotBeNull();
        script.Should().Contain("https://x-access-token:ghp_fallback@github.com");
        script.Should().Contain("git config --global credential.helper store");
        script.Should().Contain("export GH_TOKEN=");
        script.Should().Contain("ghp_fallback");
    }
}
