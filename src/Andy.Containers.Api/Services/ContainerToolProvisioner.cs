using Andy.Containers.Abstractions;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Ensures runtime CLIs the agent + verifier rely on are present inside a
/// container. Today that's the GitHub CLI (<c>gh</c>), which both the
/// PR-author agent (<c>gh pr create</c>) and the PR-deliverable verifier
/// (<c>gh pr view</c>) shell out to.
///
/// <para>
/// Why this exists (2026-06-29): the base agent image is bare
/// <c>ubuntu:24.04</c> and the provisioning <c>PostCreateScript</c>'s gh
/// install was brittle — it derived the version from an UNAUTHENTICATED
/// <c>api.github.com</c> call (rate-limited → empty version → malformed URL)
/// and swallowed every failure with <c>|| true</c>. A long-lived task
/// container that missed it could never create a PR; <c>gh pr view</c> failed
/// with <c>gh: not found</c>, which the verifier reported as the misleading
/// <c>[PR-VERIFY-002] "found no open PR"</c>.
/// </para>
///
/// The install (<see cref="GitHubCliInstallScript"/>) is idempotent
/// (<c>command -v gh || …</c>) and reliable: it uses the official GitHub CLI
/// apt keyring repository on Debian/Ubuntu, with a tarball fallback for other
/// distros. It is invoked at run dispatch (<see cref="HeadlessRunner"/>) so it
/// covers BOTH new containers and pre-existing ones, and it is reused by the
/// provisioning <c>PostCreateScript</c> so the common path installs up front.
/// </summary>
public interface IContainerToolProvisioner
{
    /// <summary>
    /// Ensure <c>gh</c> is installed. Idempotent + best-effort: returns
    /// <c>true</c> when gh is present afterwards, <c>false</c> (logged) when
    /// the install could not complete (e.g. no network) — the caller proceeds
    /// either way so a transient install failure never blocks a run.
    /// </summary>
    Task<bool> EnsureGitHubCliAsync(Guid containerId, CancellationToken ct = default);
}

public sealed class ContainerToolProvisioner : IContainerToolProvisioner
{
    private readonly IContainerService _containers;
    private readonly ILogger<ContainerToolProvisioner> _logger;

    public ContainerToolProvisioner(
        IContainerService containers,
        ILogger<ContainerToolProvisioner> logger)
    {
        _containers = containers;
        _logger = logger;
    }

    /// <summary>
    /// Reliable, idempotent <c>gh</c> install. Runs as the container's exec
    /// user (root in the agent images, which is what apt / <c>/usr/local/bin</c>
    /// need). Shared verbatim with the provisioning <c>PostCreateScript</c>.
    ///
    /// Strategy:
    /// 1. No-op if <c>gh</c> already resolves.
    /// 2. Debian/Ubuntu: install from the official GitHub CLI apt repo
    ///    (keyring + source list) — the canonical, version-stable method.
    /// 3. Fallback (non-apt distros, or the apt repo unreachable): download
    ///    the release tarball and drop <c>gh</c> into <c>/usr/local/bin</c>.
    /// Errors are NOT swallowed silently — the final <c>command -v gh</c>
    /// drives the exit code so the caller can log a real failure.
    /// </summary>
    public const string GitHubCliInstallScript =
        "command -v gh >/dev/null 2>&1 || { " +
            "if command -v apt-get >/dev/null 2>&1; then " +
                "export DEBIAN_FRONTEND=noninteractive; " +
                "apt-get update -qq >/dev/null 2>&1; " +
                "apt-get install -y -qq curl ca-certificates >/dev/null 2>&1; " +
                "curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg " +
                    "-o /usr/share/keyrings/githubcli-archive-keyring.gpg && " +
                "chmod go+r /usr/share/keyrings/githubcli-archive-keyring.gpg && " +
                "echo \"deb [arch=$(dpkg --print-architecture) " +
                    "signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] " +
                    "https://cli.github.com/packages stable main\" " +
                    "> /etc/apt/sources.list.d/github-cli.list && " +
                "apt-get update -qq >/dev/null 2>&1 && " +
                "apt-get install -y -qq gh >/dev/null 2>&1; " +
            "fi; " +
            "command -v gh >/dev/null 2>&1 || { " +
                "GHARCH=$(uname -m | sed 's/x86_64/amd64/;s/aarch64/arm64/'); " +
                "GHVER=$(curl -fsSL https://api.github.com/repos/cli/cli/releases/latest " +
                    "2>/dev/null | grep -o '\"tag_name\": *\"v[^\"]*' | head -1 | sed 's/.*v//'); " +
                "[ -n \"$GHVER\" ] && curl -fsSL " +
                    "\"https://github.com/cli/cli/releases/download/v${GHVER}/gh_${GHVER}_linux_${GHARCH}.tar.gz\" " +
                    "2>/dev/null | tar xz -C /tmp 2>/dev/null && " +
                "cp /tmp/gh_${GHVER}_linux_${GHARCH}/bin/gh /usr/local/bin/gh 2>/dev/null && " +
                "chmod +x /usr/local/bin/gh 2>/dev/null; " +
            "}; " +
        "}; " +
        "command -v gh >/dev/null 2>&1";

    public async Task<bool> EnsureGitHubCliAsync(Guid containerId, CancellationToken ct = default)
    {
        var result = await _containers.ExecAsync(
            containerId, GitHubCliInstallScript, TimeSpan.FromMinutes(3), ct);

        if (result.ExitCode == 0)
        {
            return true;
        }

        _logger.LogWarning(
            "GitHub CLI (gh) is not available in container {ContainerId} and could not be installed " +
            "(exit {ExitCode}): {StdErr}. gh pr create / gh pr view will fail.",
            containerId, result.ExitCode, result.StdErr);
        return false;
    }
}
