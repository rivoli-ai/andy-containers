// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Containers.Models;

/// <summary>
/// A named, first-class environment profile — the runtime shape (headless container,
/// terminal, desktop) plus its capability envelope (network, secrets, GUI, audit).
/// Agents declare the set of profiles they're allowed to run in; <c>Run</c>s pick one.
/// </summary>
/// <remarks>
/// Story X1 (rivoli-ai/andy-containers#90). See Epic X (#88) for the broader
/// EnvironmentProfile-catalog design. AP1's <see cref="Run.EnvironmentProfileId"/>
/// becomes a real FK once the seed (X2) lands and existing rows can be backfilled.
/// </remarks>
public class EnvironmentProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Slug, unique across the catalog (e.g. <c>headless-container</c>).</summary>
    public required string Name { get; set; }

    /// <summary>Human-readable label shown in catalog UIs.</summary>
    public required string DisplayName { get; set; }

    public EnvironmentKind Kind { get; set; }

    /// <summary>
    /// Container image reference the run starts from (e.g.
    /// <c>ghcr.io/rivoli-ai/andy-headless:2026.04</c>). Mutable per-profile;
    /// AP4's run configurator resolves it at run time.
    /// </summary>
    public required string BaseImageRef { get; set; }

    public EnvironmentCapabilities Capabilities { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Runtime shape of a profile. Mirrors <see cref="RunMode"/> but lives on the
/// catalog: a profile of kind <see cref="HeadlessContainer"/> can only back
/// <see cref="RunMode.Headless"/> runs (X5 enforces the pairing on workspace
/// create).
/// </summary>
public enum EnvironmentKind
{
    HeadlessContainer,
    Terminal,
    Desktop
}

/// <summary>
/// Capability envelope for a profile. Stored as a single JSON column so the
/// shape can grow (e.g. new capability flags) without a migration per field.
/// </summary>
public class EnvironmentCapabilities
{
    /// <summary>
    /// Hostnames or CIDR ranges the run is permitted to reach. Empty list =
    /// no outbound network. Wildcards (<c>*.github.com</c>) follow the same
    /// semantics as the container provider's network policy.
    /// </summary>
    public List<string> NetworkAllowlist { get; set; } = new();

    public SecretsScope SecretsScope { get; set; } = SecretsScope.RunScoped;

    /// <summary>True if the profile attaches a graphical session (X server, VNC).</summary>
    public bool HasGui { get; set; }

    public AuditMode AuditMode { get; set; } = AuditMode.Standard;
}

/// <summary>
/// What credentials the run can request. Couples to AP10's run-scoped token
/// wiring: <see cref="RunScoped"/> tokens are revoked on terminal events;
/// broader scopes are not.
/// </summary>
public enum SecretsScope
{
    None,
    RunScoped,
    WorkspaceScoped,
    OrganizationScoped
}

/// <summary>
/// Audit-trail intensity for the profile. <see cref="Strict"/> implies every
/// tool call is captured pre-redaction; <see cref="Standard"/> matches the
/// default action-log behaviour; <see cref="None"/> disables audit emission
/// (only valid for sandboxed dev profiles, never production runs).
/// </summary>
public enum AuditMode
{
    None,
    Standard,
    Strict
}
