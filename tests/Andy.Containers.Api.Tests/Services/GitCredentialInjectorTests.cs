using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

/// <summary>
/// #1046. Pure-function coverage of the shell-script builder used by
/// <see cref="ContainerProvisioningWorker"/> to materialise a user's
/// git credentials inside a freshly-provisioned container.
/// </summary>
public class GitCredentialInjectorTests
{
    // -------------------------------------------------------------
    // Empty / no-op paths
    // -------------------------------------------------------------

    [Fact]
    public void Build_NoCredentials_ReturnsNull()
    {
        var script = GitCredentialInjector.BuildInjectionScript("alice", []);
        script.Should().BeNull(
            "no work to do means no shell command at all — the worker should skip ExecAsync.");
    }

    [Fact]
    public void Build_PatWithoutGitHost_IsSkipped()
    {
        // Hostless PATs can't form a useful `https://<token>@<host>` line.
        // We skip them rather than emit an invalid entry that would
        // confuse `git config --global credential.helper store`.
        var creds = new[]
        {
            new DecryptedGitCredential(
                Guid.NewGuid(),
                Label: "ghost",
                GitHost: null,
                CredentialType: GitCredentialType.PersonalAccessToken,
                PlaintextToken: "abc"),
        };
        var script = GitCredentialInjector.BuildInjectionScript("alice", creds);
        script.Should().BeNull();
    }

    // -------------------------------------------------------------
    // PAT injection
    // -------------------------------------------------------------

    [Fact]
    public void Build_SinglePat_EmitsGitCredentialsLineAndHelper()
    {
        var creds = new[]
        {
            new DecryptedGitCredential(
                Guid.NewGuid(),
                Label: "github",
                GitHost: "github.com",
                CredentialType: GitCredentialType.PersonalAccessToken,
                PlaintextToken: "ghp_xyz"),
        };

        var script = GitCredentialInjector.BuildInjectionScript("alice", creds);
        script.Should().NotBeNull();
        script.Should().StartWith("su - alice -c '",
            "running as the target user is what makes ~/.git-credentials land in the right home.");
        script.Should().Contain("https://x-access-token:ghp_xyz@github.com");
        script.Should().Contain("chmod 0600 ~/.git-credentials");
        script.Should().Contain("git config --global credential.helper store");
    }

    [Fact]
    public void Build_OAuthToken_UsesOauth2Username()
    {
        // The oauth2:<token>@host form is what GitHub & GitLab expect
        // for OAuth-issued tokens; using `x-access-token` would still
        // work for GitHub but breaks on GitLab.
        var creds = new[]
        {
            new DecryptedGitCredential(
                Guid.NewGuid(),
                Label: "gitlab-oauth",
                GitHost: "gitlab.com",
                CredentialType: GitCredentialType.OAuthToken,
                PlaintextToken: "oat_abc"),
        };

        var script = GitCredentialInjector.BuildInjectionScript("alice", creds);
        script.Should().Contain("https://oauth2:oat_abc@gitlab.com");
    }

    [Fact]
    public void Build_TokenWithReservedChars_IsPercentEncoded()
    {
        // Tokens with `:`, `@`, `/`, etc. must be percent-encoded so
        // git's URL parser doesn't get confused about where the
        // username/host boundary lies.
        var creds = new[]
        {
            new DecryptedGitCredential(
                Guid.NewGuid(),
                Label: "tricky",
                GitHost: "github.com",
                CredentialType: GitCredentialType.PersonalAccessToken,
                PlaintextToken: "abc:de@fg/h"),
        };

        var script = GitCredentialInjector.BuildInjectionScript("alice", creds);
        // `:` → `%3A`, `@` → `%40`, `/` → `%2F`
        script.Should().Contain("abc%3Ade%40fg%2Fh");
        script.Should().NotContain("abc:de@fg/h",
                "the raw token must not appear in the URL — it would break the parser.");
    }

    // -------------------------------------------------------------
    // DeployKey injection
    // -------------------------------------------------------------

    [Fact]
    public void Build_DeployKey_EmitsKeyFileAndConfigStanza()
    {
        const string pem =
            "-----BEGIN OPENSSH PRIVATE KEY-----\n" +
            "b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQ==\n" +
            "-----END OPENSSH PRIVATE KEY-----";
        var creds = new[]
        {
            new DecryptedGitCredential(
                Guid.NewGuid(),
                Label: "deploy-foo",
                GitHost: "github.com",
                CredentialType: GitCredentialType.DeployKey,
                PlaintextToken: pem),
        };

        var script = GitCredentialInjector.BuildInjectionScript("alice", creds);
        script.Should().NotBeNull();
        script.Should().Contain("mkdir -p ~/.ssh");
        script.Should().Contain("chmod 0700 ~/.ssh");
        script.Should().Contain("cat > ~/.ssh/id_deploy-foo <<");
        script.Should().Contain("chmod 0600 ~/.ssh/id_deploy-foo");
        script.Should().Contain("Host github.com");
        script.Should().Contain("IdentityFile ~/.ssh/id_deploy-foo");
        script.Should().Contain("IdentitiesOnly yes");
    }

    [Fact]
    public void Build_DeployKeyHostless_OmitsConfigStanza()
    {
        // Hostless deploy keys still get written so an interactive user
        // can opt in via `ssh-add`, but we don't synthesise a Host
        // stanza — there's no host to bind it to.
        var creds = new[]
        {
            new DecryptedGitCredential(
                Guid.NewGuid(),
                Label: "ambient",
                GitHost: null,
                CredentialType: GitCredentialType.DeployKey,
                PlaintextToken: "-----BEGIN OPENSSH PRIVATE KEY-----\nzzz\n-----END OPENSSH PRIVATE KEY-----"),
        };

        var script = GitCredentialInjector.BuildInjectionScript("alice", creds);
        script.Should().NotBeNull();
        script.Should().Contain("cat > ~/.ssh/id_ambient <<");
        script.Should().NotContain("Host ",
                "no GitHost means no per-host stanza — IdentityFile binding is up to the user / ssh-agent.");
    }

    [Fact]
    public void Build_DeployKeyLabelWithUnsafeChars_IsSanitised()
    {
        // A label like `prod/key` would inject `~/.ssh/id_prod/key` —
        // i.e. attempt to write into a `prod` subdirectory that doesn't
        // exist yet. Strip path separators + anything outside the safe
        // alphabet so the filename always lands directly in `~/.ssh/`.
        var creds = new[]
        {
            new DecryptedGitCredential(
                Guid.NewGuid(),
                Label: "prod/key with spaces",
                GitHost: "github.com",
                CredentialType: GitCredentialType.DeployKey,
                PlaintextToken: "-----BEGIN OPENSSH PRIVATE KEY-----\nz\n-----END OPENSSH PRIVATE KEY-----"),
        };

        var script = GitCredentialInjector.BuildInjectionScript("alice", creds);
        script.Should().NotBeNull();
        script.Should().Contain("cat > ~/.ssh/id_prodkeywithspaces <<",
                "directory separators and whitespace must be stripped from the filename.");
        script.Should().NotContain("~/.ssh/id_prod/key",
                "the filename must never contain a `/` after the `id_` prefix.");
    }

    [Fact]
    public void Build_DeployKeyWithEmptyLabel_DegradesToDefault()
    {
        var creds = new[]
        {
            new DecryptedGitCredential(
                Guid.NewGuid(),
                Label: "",
                GitHost: null,
                CredentialType: GitCredentialType.DeployKey,
                PlaintextToken: "-----BEGIN OPENSSH PRIVATE KEY-----\nz\n-----END OPENSSH PRIVATE KEY-----"),
        };

        var script = GitCredentialInjector.BuildInjectionScript("alice", creds);
        script.Should().Contain("~/.ssh/id_deploykey",
                "empty / fully-stripped labels degrade to a stable default rather than producing a `~/.ssh/id_` filename.");
    }

    // -------------------------------------------------------------
    // Mixed credentials
    // -------------------------------------------------------------

    [Fact]
    public void Build_PatPlusDeployKey_EmitsBothBlocks()
    {
        var creds = new[]
        {
            new DecryptedGitCredential(
                Guid.NewGuid(),
                Label: "github",
                GitHost: "github.com",
                CredentialType: GitCredentialType.PersonalAccessToken,
                PlaintextToken: "ghp_xyz"),
            new DecryptedGitCredential(
                Guid.NewGuid(),
                Label: "deploy",
                GitHost: "git.internal",
                CredentialType: GitCredentialType.DeployKey,
                PlaintextToken: "-----BEGIN OPENSSH PRIVATE KEY-----\nz\n-----END OPENSSH PRIVATE KEY-----"),
        };

        var script = GitCredentialInjector.BuildInjectionScript("alice", creds);
        script.Should().NotBeNull();
        script.Should().Contain("https://x-access-token:ghp_xyz@github.com");
        script.Should().Contain("Host git.internal");
        script.Should().Contain("git config --global credential.helper store");
    }

    // -------------------------------------------------------------
    // Wrapping shell escapes
    // -------------------------------------------------------------

    [Fact]
    public void Build_WrapsInSuToContainerUser()
    {
        // The whole script must run as the container user so the
        // filesystem ownership matches. `su - <user> -c '...'` is the
        // convention ContainerProvisioningWorker already uses for its
        // git config block.
        var creds = new[]
        {
            new DecryptedGitCredential(
                Guid.NewGuid(),
                Label: "github",
                GitHost: "github.com",
                CredentialType: GitCredentialType.PersonalAccessToken,
                PlaintextToken: "tok"),
        };

        var script = GitCredentialInjector.BuildInjectionScript("dev-user", creds);
        script.Should().StartWith("su - dev-user -c '");
        script.Should().EndWith("'");
    }

    [Fact]
    public void Build_RejectsEmptyContainerUser()
    {
        var creds = new[]
        {
            new DecryptedGitCredential(
                Guid.NewGuid(),
                Label: "github",
                GitHost: "github.com",
                CredentialType: GitCredentialType.PersonalAccessToken,
                PlaintextToken: "tok"),
        };

        Action call = () => GitCredentialInjector.BuildInjectionScript("", creds);
        call.Should().Throw<ArgumentException>(
                "an empty container user means we'd run as root and the resulting files would be owned by root — refuse rather than silently mis-place files.");
    }
}
