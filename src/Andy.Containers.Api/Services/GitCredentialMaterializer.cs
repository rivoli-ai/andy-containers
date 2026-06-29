using Andy.Containers.Abstractions;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Materialises git credentials INTO a running container — the owner's
/// registered credentials merged with the andy-settings
/// <c>sourceControl.github.pat</c> fallback (rivoli-ai/conductor#2242) — and
/// execs the injection script so <c>git push</c> / <c>gh pr create</c>
/// authenticate.
///
/// Single source of truth shared by BOTH credential-injection sites:
/// <list type="bullet">
/// <item><see cref="ContainerProvisioningWorker"/> at container-create time, and</item>
/// <item><see cref="HeadlessRunner"/> at run-dispatch time.</item>
/// </list>
///
/// The run-dispatch call is the fix for the dead-credential class
/// (2026-06-29): the PAT used to be injected ONLY at provisioning, so a
/// long-lived task container (one-per-workspace, FS-continuous across a goal's
/// tasks) provisioned BEFORE the operator saved <c>sourceControl.github.pat</c>
/// could never authenticate — every PR-author run committed locally but failed
/// to push, surfacing <c>[PR-VERIFY-002]</c>. Re-materialising at each dispatch
/// means a PAT saved after the container exists still reaches it. The injection
/// is idempotent (<see cref="GitCredentialInjector.BuildInjectionScript"/>
/// overwrites <c>~/.git-credentials</c> and grep-guards the
/// <c>~/.bashrc</c> GH_TOKEN export), so running it every dispatch is safe.
/// </summary>
public interface IGitCredentialMaterializer
{
    /// <summary>
    /// Resolve + inject credentials into <paramref name="containerId"/>.
    /// Best-effort and self-contained: it never throws for an expected
    /// failure (no PAT configured, no container user, exec non-zero) — those
    /// are logged and reflected in the returned result so the caller can keep
    /// going. Only genuinely unexpected exceptions propagate.
    /// </summary>
    Task<GitCredentialMaterializationResult> MaterializeAsync(
        Guid containerId, string? containerUser, string? ownerId, CancellationToken ct = default);
}

/// <summary>Outcome of a credential-materialisation attempt (for logging/tests).</summary>
public readonly record struct GitCredentialMaterializationResult(
    int CredentialCount, bool UsedSettingsPatFallback, bool Injected);

public sealed class GitCredentialMaterializer : IGitCredentialMaterializer
{
    private readonly IGitCredentialService _credentials;
    private readonly ISourceControlSecretResolver _secretResolver;
    private readonly IContainerService _containers;
    private readonly ILogger<GitCredentialMaterializer> _logger;

    public GitCredentialMaterializer(
        IGitCredentialService credentials,
        ISourceControlSecretResolver secretResolver,
        IContainerService containers,
        ILogger<GitCredentialMaterializer> logger)
    {
        _credentials = credentials;
        _secretResolver = secretResolver;
        _containers = containers;
        _logger = logger;
    }

    public async Task<GitCredentialMaterializationResult> MaterializeAsync(
        Guid containerId, string? containerUser, string? ownerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(containerUser))
        {
            _logger.LogWarning(
                "Cannot materialise git credentials into container {ContainerId}: no container user.",
                containerId);
            return new GitCredentialMaterializationResult(0, false, false);
        }

        // The owner's registered credentials (if any). For andy-tasks-spawned
        // run containers the owner is the andy-tasks-api M2M principal, which
        // holds no user git credentials, so this is typically empty and the
        // settings-PAT fallback below is what authenticates.
        var userCredentials = new List<DecryptedGitCredential>();
        if (!string.IsNullOrEmpty(ownerId))
        {
            try
            {
                userCredentials.AddRange(await _credentials.ListWithDecryptedTokensAsync(ownerId, ct));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to list git credentials for owner {OwnerId} (container {ContainerId}); continuing with the settings-PAT fallback only.",
                    ownerId, containerId);
            }
        }

        // Only consult andy-settings for the fallback PAT when the owner has
        // NO github.com credential of their own — mirrors the create-time path
        // and avoids a needless secret round-trip when a real credential exists.
        string? pat = null;
        if (!userCredentials.Any(ContainerProvisioningWorker.IsGitHubCredential))
        {
            try
            {
                pat = await _secretResolver.GetGitHubPatAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to resolve sourceControl.github.pat from andy-settings for container {ContainerId} — proceeding without a GitHub fallback credential.",
                    containerId);
            }
        }

        // Single-sourced merge decision (fallback iff no owner github cred).
        var merged = ContainerProvisioningWorker.ResolveContainerCredentials(userCredentials, pat);
        var usedFallback = merged.Count > userCredentials.Count;

        var script = GitCredentialInjector.BuildInjectionScript(containerUser!, merged);
        if (script is null)
        {
            _logger.LogWarning(
                "No git credential available for container {ContainerId} (no owner credential and no sourceControl.github.pat). " +
                "git push / gh pr create will fail; set the sourceControl.github.pat secret (a PAT with 'repo' scope) to enable PR creation.",
                containerId);
            return new GitCredentialMaterializationResult(merged.Count, usedFallback, false);
        }

        var result = await _containers.ExecAsync(containerId, script, TimeSpan.FromMinutes(1), ct);
        if (result.ExitCode != 0)
        {
            _logger.LogWarning(
                "Git credential injection exited with {ExitCode} for container {ContainerId}: {StdErr}",
                result.ExitCode, containerId, result.StdErr);
            return new GitCredentialMaterializationResult(merged.Count, usedFallback, false);
        }

        // The block above wraps its writes in `su - {containerUser}` and exports
        // GH_TOKEN via ~/.bashrc — which only an INTERACTIVE LOGIN shell sources.
        // But the PR-author agent and the PR verifier run `git`/`gh` via a bare,
        // NON-login `sh -c` as the container's exec user (root in the agent
        // images), so neither the containerUser's files nor the ~/.bashrc export
        // are visible to them — gh reports "populate the GH_TOKEN environment
        // variable" and the verifier fails [PR-VERIFY-001]. Deliver the GitHub
        // credential ENV-INDEPENDENTLY to the exec user: git's credential-store
        // (read via ~/.gitconfig, no env) and gh's own ~/.config/gh/hosts.yml
        // (read unconditionally, no env). Idempotent (files overwritten).
        var gitHub = merged.FirstOrDefault(ContainerProvisioningWorker.IsGitHubCredential);
        if (gitHub is not null)
        {
            var rootScript = BuildExecUserGitHubAuthScript(gitHub.PlaintextToken);
            var rootResult = await _containers.ExecAsync(containerId, rootScript, TimeSpan.FromMinutes(1), ct);
            if (rootResult.ExitCode != 0)
            {
                _logger.LogWarning(
                    "Env-independent gh/git credential setup for the exec user exited {ExitCode} in container {ContainerId}: {StdErr}",
                    rootResult.ExitCode, containerId, rootResult.StdErr);
            }
        }

        _logger.LogInformation(
            "Materialised {Count} git credential(s) into container {ContainerId} as user {User} (settings-PAT fallback: {Fallback}).",
            merged.Count, containerId, containerUser, usedFallback);
        return new GitCredentialMaterializationResult(merged.Count, usedFallback, true);
    }

    /// <summary>
    /// Builds a script (run as the container's exec user — root in the agent
    /// images) that authenticates BOTH <c>git</c> and <c>gh</c> for a
    /// non-login <c>sh -c</c> invocation, with NO reliance on environment vars:
    /// <list type="bullet">
    /// <item><c>git</c>: <c>credential.helper store</c> + <c>$HOME/.git-credentials</c>
    /// (git reads <c>~/.gitconfig</c> + the store file regardless of shell).</item>
    /// <item><c>gh</c>: <c>$HOME/.config/gh/hosts.yml</c> (gh reads its config file
    /// unconditionally — this is the env-independent equivalent of GH_TOKEN).</item>
    /// </list>
    /// </summary>
    internal static string BuildExecUserGitHubAuthScript(string token)
    {
        var t = ShellSingleQuote(token);
        return
            "set -e; " +
            "H=${HOME:-/root}; " +
            "git config --global credential.helper store; " +
            $"printf 'https://x-access-token:%s@github.com\\n' {t} > \"$H/.git-credentials\"; " +
            "chmod 600 \"$H/.git-credentials\"; " +
            "mkdir -p \"$H/.config/gh\"; " +
            $"printf 'github.com:\\n    oauth_token: %s\\n    git_protocol: https\\n' {t} > \"$H/.config/gh/hosts.yml\"; " +
            "chmod 600 \"$H/.config/gh/hosts.yml\"";
    }

    /// <summary>POSIX single-quote escaping: wrap in '…', rendering any
    /// embedded single quote as the standard <c>'\''</c> sequence.</summary>
    private static string ShellSingleQuote(string value)
        => "'" + value.Replace("'", "'\\''") + "'";
}
