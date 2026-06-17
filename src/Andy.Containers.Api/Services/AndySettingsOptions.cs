// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Containers.Api.Services;

/// <summary>
/// rivoli-ai/conductor#2242. Configuration for the andy-settings HTTP client
/// (<see cref="AndySettingsHttpClient"/>) used to resolve the source-control
/// GitHub PAT (<c>sourceControl.github.pat</c>) as a FALLBACK credential to
/// inject into a task container when the user has registered no per-host git
/// credential of their own. Bound from <c>AndySettings:</c> in
/// <c>appsettings.json</c> / environment overrides.
///
/// <para>
/// When <see cref="ApiBaseUrl"/> is empty the client is NOT registered — the
/// credential-injection step falls back to the user's own registered
/// credentials only (the pre-#2242 posture), so dev / embedded mode without an
/// andy-settings instance reachable does NOT fail at startup. Mirrors the
/// <c>AndyAgents:</c> / <c>AndyDocs:</c> wiring posture exactly.
/// </para>
/// </summary>
public sealed class AndySettingsOptions
{
    public const string SectionName = "AndySettings";

    /// <summary>
    /// Base URL of the andy-settings API — e.g. <c>http://localhost:5310/</c>
    /// in dev or, in Conductor embedded mode, the unified proxy route
    /// <c>http://localhost:9100/settings</c>. Empty / null disables the
    /// settings-backed PAT fallback entirely.
    /// </summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>Per-request HttpClient timeout for a single secret lookup.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Audience the M2M bearer is minted with. Reading a machine-scope secret
    /// requires the andy-settings API audience.
    /// </summary>
    public string Audience { get; set; } = "urn:andy-settings-api";

    /// <summary>
    /// The andy-settings secret key holding the GitHub Personal Access Token
    /// used for source-control operations. Fixed by the platform contract; an
    /// option only so tests / non-default deployments can override it.
    /// </summary>
    public string GitHubPatKey { get; set; } = "sourceControl.github.pat";
}
