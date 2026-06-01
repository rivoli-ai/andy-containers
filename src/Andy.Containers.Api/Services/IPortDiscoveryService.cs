using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Discovers the TCP ports relevant to a run's container: those published
/// to a host port (so Conductor can preview a web app over the UnifiedProxy
/// loopback surface) merged with those discovered listening inside the
/// container via <c>ss -ltn</c> / <c>netstat</c> run through the
/// infrastructure provider's exec surface — never a new Docker-Engine verb
/// (decision #17).
///
/// Story F6.4 (rivoli-ai/conductor#1943).
/// </summary>
public interface IPortDiscoveryService
{
    /// <summary>
    /// List the mapped + discovered ports for <paramref name="containerId"/>.
    /// A stopped container / no-listening-port yields an empty-but-OK result
    /// (no ports), never an error. A live-probe failure degrades gracefully
    /// to the statically-mapped ports.
    /// </summary>
    Task<ContainerPortsResult> GetPortsAsync(Guid containerId, CancellationToken ct = default);

    /// <summary>
    /// Publishes a container port to a host (loopback) port for the run's web
    /// preview, returning the mapping. Throws
    /// <see cref="NotSupportedException"/> when the provider can't add a live
    /// mapping (the API surfaces it as a 400).
    /// </summary>
    Task<MappedPort> ExposePortAsync(Guid containerId, int containerPort, CancellationToken ct = default);
}
