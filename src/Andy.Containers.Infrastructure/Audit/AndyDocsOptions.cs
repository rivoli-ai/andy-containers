// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Containers.Infrastructure.Audit;

/// <summary>
/// rivoli-ai/andy-containers#320. Configuration for the andy-docs
/// HTTP client used by the output-artifact collector. Bound from
/// <c>AndyDocs:</c> in <c>appsettings.json</c> / environment
/// overrides.
///
/// <para>
/// When <see cref="ApiBaseUrl"/> is empty the client is NOT registered
/// — the collector operates in metadata-only mode, matching the
/// pre-#320 wire shape. This mirrors the andy-tasks AndyDocs wiring
/// posture so dev / embedded mode without an andy-docs instance does
/// not fail at startup.
/// </para>
/// </summary>
public sealed class AndyDocsOptions
{
    public const string SectionName = "AndyDocs";

    /// <summary>
    /// Base URL of the andy-docs API — e.g.
    /// <c>http://localhost:5450/</c> in dev or
    /// <c>https://andy-docs.example.com/</c> in production. Empty / null
    /// disables the client entirely (collector falls back to metadata-
    /// only mode).
    /// </summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>
    /// Per-request HttpClient timeout. Caps the slowest single upload
    /// before the bearer-handler timeout / caller cancel fires.
    /// Defaults to 30s — generous for a single artifact (the per-file
    /// upload cap is 64 MiB) without holding the terminal-event path
    /// open indefinitely.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Audience the M2M bearer should be minted with. Defaults to the
    /// andy-docs API audience as registered in andy-auth's seed.
    /// </summary>
    public string Audience { get; set; } = "urn:andy-docs-api";
}
