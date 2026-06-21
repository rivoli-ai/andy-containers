using System.Runtime.InteropServices;

namespace Andy.Containers.Infrastructure.Registries.Local;

/// <summary>
/// Resolves the <em>push/tag target authority</em> (<c>host:port</c>)
/// that the Docker CLI is told to push to, which is NOT always the
/// same authority Conductor's HTTP API client uses to talk to zot.
/// </summary>
/// <remarks>
/// <para>
/// <b>The Docker Desktop loopback gap (rivoli-ai/andy-containers).</b>
/// The embedded zot registry binds the host's loopback interface
/// (<c>127.0.0.1:5050</c>) and Conductor's in-process HTTP reads talk
/// to <c>http://localhost:5050</c> — both correct, both on the host.
/// But <c>docker push localhost:5050/...</c> runs <em>inside the Docker
/// Desktop VM</em>, where <c>localhost</c> is the VM, not the host. The
/// VM has no route to the host's loopback, so the push hangs and dies
/// with:
/// </para>
/// <code>
/// Get "http://localhost:5050/v2/": net/http: request canceled while
/// waiting for connection (Client.Timeout exceeded while awaiting headers)
/// </code>
/// <para>
/// even though <c>curl http://localhost:5050/v2/</c> returns 200 from
/// the host. The fix is to point the daemon's push/tag at a
/// VM-reachable address — Docker Desktop publishes the host as
/// <c>host.docker.internal</c> — while the host-side HTTP client keeps
/// using <c>localhost</c>. This resolver owns that rewrite.
/// </para>
/// <para>
/// <b>Two extra preconditions the rewrite depends on</b> (outside this
/// type's control, surfaced via <see cref="PushTargetHostResolution"/>
/// so the call sites can warn loudly):
/// <list type="number">
/// <item>zot must bind a VM-reachable interface (e.g. <c>0.0.0.0:5050</c>,
/// not loopback-only) — owned by Conductor's zot launch config.</item>
/// <item><c>host.docker.internal:5050</c> is plain HTTP, and Docker
/// treats every non-<c>localhost</c> registry as HTTPS by default, so
/// the host must be in the daemon's <c>insecure-registries</c> list.</item>
/// </list>
/// </para>
/// <para>
/// The rewrite is gated on detecting Docker Desktop (Docker engine on
/// macOS/Windows) or an explicit config flag, so native Linux Docker —
/// where <c>localhost</c> in the daemon IS the host — is left unchanged.
/// </para>
/// </remarks>
public static class PushTargetHostResolver
{
    /// <summary>
    /// The hostname Docker Desktop publishes for "the host running the
    /// daemon", reachable from inside the VM and from containers.
    /// </summary>
    public const string DockerDesktopHostAlias = "host.docker.internal";

    /// <summary>
    /// Loopback host tokens that the Docker Desktop VM cannot route to.
    /// Only these are rewritten; an already-routable authority
    /// (a LAN IP, a real hostname, <c>host.docker.internal</c> itself)
    /// is left untouched.
    /// </summary>
    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "::1", "[::1]"];

    /// <summary>
    /// Resolve the authority the Docker CLI should tag/push to, given
    /// the registry's configured authority (the one the HTTP client
    /// uses) and the chosen rewrite policy.
    /// </summary>
    /// <param name="registryAuthority">
    /// The registry authority as the HTTP client sees it, e.g.
    /// <c>localhost:5050</c> or <c>127.0.0.1:5050</c>. Must not be
    /// null/whitespace.
    /// </param>
    /// <param name="options">Rewrite policy (mode + alias host).</param>
    /// <returns>
    /// The resolution: the target authority for <c>docker push</c> plus
    /// whether a rewrite happened (so the caller can warn about the
    /// <c>insecure-registries</c> precondition).
    /// </returns>
    public static PushTargetHostResolution Resolve(
        string registryAuthority,
        PushTargetHostOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryAuthority);
        ArgumentNullException.ThrowIfNull(options);

        var (host, portSuffix) = SplitAuthority(registryAuthority);

        var shouldRewrite = options.Mode switch
        {
            PushTargetHostRewriteMode.Never => false,
            PushTargetHostRewriteMode.Always => true,
            // Auto: rewrite only when the daemon is the Docker Desktop
            // VM — i.e. Docker engine on a non-Linux host. On Linux the
            // daemon shares the host's network namespace and localhost
            // is the host, so a rewrite would break it.
            PushTargetHostRewriteMode.Auto => IsDockerDesktop(options),
            _ => false,
        };

        if (!shouldRewrite || !IsLoopback(host))
        {
            return new PushTargetHostResolution(
                TargetAuthority: registryAuthority,
                WasRewritten: false,
                AliasHost: options.DockerDesktopHostAlias);
        }

        var alias = string.IsNullOrWhiteSpace(options.DockerDesktopHostAlias)
            ? DockerDesktopHostAlias
            : options.DockerDesktopHostAlias.Trim();

        return new PushTargetHostResolution(
            TargetAuthority: $"{alias}{portSuffix}",
            WasRewritten: true,
            AliasHost: alias);
    }

    /// <summary>
    /// Rewrite a full remote reference (<c>host:port/repo:tag</c>) so
    /// only the authority segment changes. The repo path and tag are
    /// preserved verbatim.
    /// </summary>
    public static PushTargetHostResolution ResolveRemoteReference(
        string remoteReference,
        PushTargetHostOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteReference);
        ArgumentNullException.ThrowIfNull(options);

        var slash = remoteReference.IndexOf('/');
        // No slash means there's no repo path to preserve — treat the
        // whole thing as the authority.
        if (slash < 0)
        {
            return Resolve(remoteReference, options);
        }

        var authority = remoteReference[..slash];
        var rest = remoteReference[slash..]; // includes leading '/'

        var resolution = Resolve(authority, options);
        if (!resolution.WasRewritten)
        {
            return resolution with { TargetAuthority = remoteReference };
        }

        return resolution with { TargetAuthority = resolution.TargetAuthority + rest };
    }

    /// <summary>
    /// True when the configured daemon is the Docker Desktop VM. Docker
    /// Desktop runs on macOS and Windows; on those hosts the daemon is
    /// in a VM with the loopback gap. On Linux, Docker runs natively and
    /// shares the host network namespace, so no rewrite is needed.
    /// </summary>
    private static bool IsDockerDesktop(PushTargetHostOptions options)
    {
        if (options.IsDockerDesktopOverride is { } forced)
        {
            return forced;
        }

        return !RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    }

    private static bool IsLoopback(string host)
        => LoopbackHosts.Contains(host, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Split <c>host:port</c> into the host and the <c>:port</c> suffix
    /// (empty when no port). Handles bracketed IPv6 literals
    /// (<c>[::1]:5050</c>).
    /// </summary>
    private static (string Host, string PortSuffix) SplitAuthority(string authority)
    {
        var trimmed = authority.Trim();

        // Bracketed IPv6: [::1]:5050 → host "[::1]", suffix ":5050".
        if (trimmed.StartsWith('['))
        {
            var close = trimmed.IndexOf(']');
            if (close >= 0)
            {
                var host = trimmed[..(close + 1)];
                var suffix = close + 1 < trimmed.Length ? trimmed[(close + 1)..] : string.Empty;
                return (host, suffix);
            }
        }

        var colon = trimmed.LastIndexOf(':');
        if (colon < 0)
        {
            return (trimmed, string.Empty);
        }
        return (trimmed[..colon], trimmed[colon..]);
    }
}

/// <summary>
/// Policy controlling when <see cref="PushTargetHostResolver"/> rewrites
/// a loopback registry authority to the Docker Desktop host alias.
/// </summary>
public enum PushTargetHostRewriteMode
{
    /// <summary>
    /// Rewrite only when the daemon is Docker Desktop (Docker engine on
    /// a non-Linux host). The default — safe on Linux, correct on
    /// macOS/Windows Docker Desktop.
    /// </summary>
    Auto = 0,

    /// <summary>Never rewrite; always push to the configured authority.</summary>
    Never = 1,

    /// <summary>Always rewrite a loopback authority, regardless of OS.</summary>
    Always = 2,
}

/// <summary>
/// Configuration for <see cref="PushTargetHostResolver"/>. Bound from
/// the <c>ImageManagement:PushTarget</c> configuration section.
/// </summary>
public sealed class PushTargetHostOptions
{
    public const string SectionName = "ImageManagement:PushTarget";

    /// <summary>When to apply the loopback→alias rewrite.</summary>
    public PushTargetHostRewriteMode Mode { get; set; } = PushTargetHostRewriteMode.Auto;

    /// <summary>
    /// The host alias the Docker Desktop daemon can reach the host on.
    /// Defaults to <c>host.docker.internal</c>.
    /// </summary>
    public string DockerDesktopHostAlias { get; set; } = PushTargetHostResolver.DockerDesktopHostAlias;

    /// <summary>
    /// Test/diagnostic override for Docker Desktop detection. When set,
    /// <see cref="PushTargetHostRewriteMode.Auto"/> uses this value
    /// instead of probing the OS platform. Null = probe the OS.
    /// </summary>
    public bool? IsDockerDesktopOverride { get; set; }
}

/// <summary>
/// Result of resolving the push/tag target authority.
/// </summary>
/// <param name="TargetAuthority">
/// The authority (or full remote ref, for the remote-ref overload) the
/// Docker CLI should be told to push to.
/// </param>
/// <param name="WasRewritten">
/// True when a loopback authority was rewritten to the Docker Desktop
/// alias — the signal that the <c>insecure-registries</c> precondition
/// now applies and a TLS/connection failure should be explained.
/// </param>
/// <param name="AliasHost">
/// The alias host used for the rewrite (e.g. <c>host.docker.internal</c>),
/// for inclusion in actionable error messages.
/// </param>
public readonly record struct PushTargetHostResolution(
    string TargetAuthority,
    bool WasRewritten,
    string AliasHost);
