// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Containers.Api.Services;

/// <summary>
/// rivoli-ai/conductor#2242. Resolves the platform-level GitHub Personal
/// Access Token (andy-settings <c>sourceControl.github.pat</c>) used as a
/// FALLBACK credential for container git operations (`git push`,
/// `gh pr create`) when the user has registered no per-host
/// <c>GitCredential</c> of their own.
///
/// <para>
/// Returns <c>null</c> when no PAT is configured (404 from andy-settings) so
/// the caller can surface a clear "GitHub credentials required" outcome rather
/// than silently proceeding with no credential. Implementations throw only on
/// a genuine connectivity / 5xx error so a transient settings outage is
/// distinguishable from "no secret set".
/// </para>
/// </summary>
public interface ISourceControlSecretResolver
{
    /// <summary>
    /// Fetch the GitHub PAT at machine scope. Null when none is set.
    /// </summary>
    Task<string?> GetGitHubPatAsync(CancellationToken ct = default);
}

/// <summary>
/// No-op resolver registered when <c>AndySettings:ApiBaseUrl</c> is empty
/// (dev / embedded mode with no settings instance). Always resolves to null,
/// so credential injection falls back to the user's own registered
/// credentials — the pre-#2242 behaviour, never a startup failure.
/// </summary>
public sealed class NullSourceControlSecretResolver : ISourceControlSecretResolver
{
    public Task<string?> GetGitHubPatAsync(CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
