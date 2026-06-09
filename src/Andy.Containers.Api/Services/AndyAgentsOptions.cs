// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Containers.Api.Services;

/// <summary>
/// AX.1 (rivoli-ai/conductor#2088). Configuration for the real andy-agents
/// HTTP client (<see cref="AndyAgentsHttpClient"/>) that resolves an agent's
/// definition — instructions + model — from the andy-agents service. Bound
/// from <c>AndyAgents:</c> in <c>appsettings.json</c> / environment overrides.
///
/// <para>
/// When <see cref="ApiBaseUrl"/> is empty the real client is NOT registered —
/// the configurator falls back to the in-process
/// <see cref="StubAndyAgentsClient"/>, matching the pre-AX.1 posture so dev /
/// embedded mode without an andy-agents instance reachable does NOT fail at
/// startup. This mirrors the <c>AndyDocs:</c> wiring posture exactly.
/// </para>
/// </summary>
public sealed class AndyAgentsOptions
{
    public const string SectionName = "AndyAgents";

    /// <summary>
    /// Base URL of the andy-agents API — e.g. <c>http://localhost:5460/</c>
    /// in dev or, in Conductor embedded mode, the unified proxy route
    /// <c>http://host.docker.internal:9100/agents</c> /
    /// <c>http://localhost:9100/agents</c>. Empty / null disables the real
    /// client entirely (configurator falls back to the stub).
    /// </summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>
    /// Per-request HttpClient timeout. Caps a single agent-resolve before the
    /// caller cancel fires. Defaults to 15s — generous for a small JSON
    /// lookup without holding the run-configuration path open indefinitely.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Audience the M2M bearer should be minted with. Defaults to the
    /// andy-agents API audience as registered in andy-auth's seed
    /// (permission <c>andy-agents:agent:read</c> on
    /// <c>GET /api/agents/by-slug/{slug}</c>).
    /// </summary>
    public string Audience { get; set; } = "urn:andy-agents-api";

    /// <summary>
    /// Default <see cref="Andy.Containers.Configurator.AgentSpecLimits.MaxIterations"/>
    /// applied to every resolved agent. andy-agents does not yet carry
    /// per-agent run limits; until it does (a later AX slice) every agent
    /// shares this options-configurable default. Defaults to 200.
    /// </summary>
    public int DefaultMaxIterations { get; set; } = 200;

    /// <summary>
    /// Default <see cref="Andy.Containers.Configurator.AgentSpecLimits.TimeoutSeconds"/>
    /// applied to every resolved agent. See <see cref="DefaultMaxIterations"/>
    /// for the per-agent-override caveat. Defaults to 1800s (30 min).
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 1800;
}
