using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

/// <summary>
/// rivoli-ai/conductor#2242. A `git push` authenticates via the
/// credential.helper store, but `gh pr create` / `gh pr view` (the PR-author
/// agent + the PR-deliverable verifier) read GH_TOKEN / GITHUB_TOKEN. Proves
/// <see cref="GitCredentialInjector"/> exports those env vars for a github.com
/// PAT/OAuth credential — and ONLY for github.com (never a different host or a
/// hostless credential).
/// </summary>
public class GitCredentialInjectorGitHubTokenTests
{
    [Fact]
    public void GitHubPat_ExportsGhTokenAndGitHubToken()
    {
        var creds = new[]
        {
            new DecryptedGitCredential(
                Guid.NewGuid(), "github", "github.com",
                GitCredentialType.PersonalAccessToken, "ghp_secret123"),
        };

        var script = GitCredentialInjector.BuildInjectionScript("alice", creds);

        script.Should().NotBeNull();
        script.Should().Contain("export GH_TOKEN=", "gh CLI reads GH_TOKEN");
        script.Should().Contain("export GITHUB_TOKEN=", "GITHUB_TOKEN is the fallback gh/Actions var");
        script.Should().Contain("ghp_secret123");
        script.Should().Contain("~/.bashrc", "the export must persist into the login shell");
        // The git push path is still wired too.
        script.Should().Contain("git config --global credential.helper store");
    }

    [Fact]
    public void GitHubOAuthToken_AlsoExportsGhToken()
    {
        var creds = new[]
        {
            new DecryptedGitCredential(
                Guid.NewGuid(), "gh-oauth", "github.com",
                GitCredentialType.OAuthToken, "gho_oauth456"),
        };

        var script = GitCredentialInjector.BuildInjectionScript("bob", creds);

        script.Should().NotBeNull();
        script.Should().Contain("export GH_TOKEN=");
        script.Should().Contain("gho_oauth456");
    }

    [Fact]
    public void NonGitHubHost_DoesNotExportGhToken()
    {
        var creds = new[]
        {
            new DecryptedGitCredential(
                Guid.NewGuid(), "gitlab", "gitlab.com",
                GitCredentialType.PersonalAccessToken, "glpat_xyz"),
        };

        var script = GitCredentialInjector.BuildInjectionScript("alice", creds);

        // Still writes the git-credentials line for gitlab, but no GH token leak.
        script.Should().NotBeNull();
        script.Should().NotContain("export GH_TOKEN=",
            "a non-GitHub credential must never be exported as a GitHub CLI token");
    }

    [Fact]
    public void HostlessPat_DoesNotExportGhToken_AndStaysNull()
    {
        // A hostless PAT can't form a git-credentials line and is not exported
        // as a GH token — so with nothing else, the script is null (no-op),
        // preserving the pre-#2242 behaviour.
        var creds = new[]
        {
            new DecryptedGitCredential(
                Guid.NewGuid(), "ghost", null,
                GitCredentialType.PersonalAccessToken, "abc"),
        };

        var script = GitCredentialInjector.BuildInjectionScript("alice", creds);
        script.Should().BeNull();
    }

    [Fact]
    public void GhTokenExport_IsIdempotent_GuardedByGrep()
    {
        // Re-provisioning must not append duplicate exports.
        var creds = new[]
        {
            new DecryptedGitCredential(
                Guid.NewGuid(), "github", "github.com",
                GitCredentialType.PersonalAccessToken, "ghp_x"),
        };

        var script = GitCredentialInjector.BuildInjectionScript("alice", creds);

        // The grep guard is present (the single quotes are shell-escaped by the
        // outer `su - user -c '...'` wrapper, so match the un-quoted substrings).
        script.Should().Contain("grep -q", "the export is guarded so a second provision doesn't duplicate it");
        script.Should().Contain("export GH_TOKEN=");
        script.Should().Contain("~/.bashrc");
    }
}
