namespace Andy.Containers.Models;

/// <summary>
/// The set of TCP ports relevant to a run's container: those already
/// published to a host port (so Conductor can reach them over the
/// <c>UnifiedProxy</c> loopback surface) plus those discovered listening
/// inside the container that may not yet be mapped.
///
/// Story F6.4 (rivoli-ai/conductor#1943). Produced by
/// <c>IPortDiscoveryService</c> by combining the provider's
/// <c>ConnectionInfo.PortMappings</c> (publish-on-create) with a live
/// <c>ss -ltn</c> / <c>netstat</c> probe run through the infrastructure
/// provider's exec surface — never a new Docker-Engine verb (decision #17).
/// </summary>
public class ContainerPortsResult
{
    /// <summary>
    /// Ports listening inside the container that are mapped to a host
    /// port (container → host). These are reachable from Conductor as
    /// <c>http://localhost:&lt;hostPort&gt;</c> via the UnifiedProxy.
    /// </summary>
    public IReadOnlyList<MappedPort> Mapped { get; set; } = new List<MappedPort>();

    /// <summary>
    /// Ports discovered listening inside the container (via <c>ss</c> /
    /// <c>netstat</c>) that are NOT mapped to a host port. The agent
    /// started a web app on one of these — Conductor can surface it but
    /// cannot reach it until it is exposed.
    /// </summary>
    public IReadOnlyList<int> DiscoveredUnmapped { get; set; } = new List<int>();

    /// <summary>
    /// The container port that most likely hosts the run's web app
    /// (first mapped listening port, else first discovered listening
    /// port; common dev ports such as 3000/5173/8000/8080 are preferred).
    /// Null when nothing is listening. Conductor defaults the Preview
    /// tab's port picker to this.
    /// </summary>
    public int? SuggestedAppPort { get; set; }
}

/// <summary>One container→host port mapping in a <see cref="ContainerPortsResult"/>.</summary>
public class MappedPort
{
    /// <summary>The port the app listens on inside the container.</summary>
    public required int ContainerPort { get; set; }

    /// <summary>The host (loopback) port the container port is published to.</summary>
    public required int HostPort { get; set; }

    /// <summary>
    /// True when the container port was also seen listening by the live
    /// <c>ss</c>/<c>netstat</c> probe (i.e. something is actually serving),
    /// false when only the static mapping is known.
    /// </summary>
    public bool Listening { get; set; }

    /// <summary>
    /// The loopback URL Conductor previews through the UnifiedProxy, e.g.
    /// <c>http://localhost:&lt;hostPort&gt;</c>.
    /// </summary>
    public string WebEndpoint => $"http://localhost:{HostPort}";
}
