using System.Globalization;
using Andy.Containers.Abstractions;
using Andy.Containers.Models;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

/// <inheritdoc cref="IPortDiscoveryService"/>
public sealed class PortDiscoveryService : IPortDiscoveryService
{
    private readonly IContainerService _containerService;
    private readonly ILogger<PortDiscoveryService> _logger;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Container ports reserved for IDE/VNC/SSH — excluded from the
    /// suggested web-app port so the picker defaults to a real app port.
    /// </summary>
    private static readonly HashSet<int> ReservedPorts = new() { 22, 6080, 8080 };

    /// <summary>
    /// Common web-dev ports, preferred when picking the suggested app port.
    /// </summary>
    private static readonly int[] PreferredAppPorts = { 3000, 5173, 8000, 4200, 5000, 8888, 80 };

    public PortDiscoveryService(IContainerService containerService, ILogger<PortDiscoveryService> logger)
    {
        _containerService = containerService;
        _logger = logger;
    }

    public async Task<ContainerPortsResult> GetPortsAsync(Guid containerId, CancellationToken ct = default)
    {
        // Static mappings come from the provider's connection info
        // (publish-on-create). Tolerate a missing/stopped container by
        // returning empty rather than throwing.
        Dictionary<int, int> mapped;
        try
        {
            var connection = await _containerService.GetConnectionInfoAsync(containerId, ct);
            mapped = connection.PortMappings is { Count: > 0 }
                ? new Dictionary<int, int>(connection.PortMappings)
                : new Dictionary<int, int>();
        }
        catch (InvalidOperationException ex)
        {
            // e.g. container has no external id yet → empty-OK.
            _logger.LogDebug(ex, "Ports: connection info unavailable for {ContainerId}; returning empty.", containerId);
            mapped = new Dictionary<int, int>();
        }

        // Live probe of listening ports inside the container. Best-effort:
        // a failed probe degrades to the static mappings only.
        var listening = await ProbeListeningPortsAsync(containerId, ct);

        var mappedPorts = mapped
            .OrderBy(kv => kv.Key)
            .Select(kv => new MappedPort
            {
                ContainerPort = kv.Key,
                HostPort = kv.Value,
                Listening = listening.Contains(kv.Key),
            })
            .ToList();

        var discoveredUnmapped = listening
            .Where(p => !mapped.ContainsKey(p))
            .OrderBy(p => p)
            .ToList();

        return new ContainerPortsResult
        {
            Mapped = mappedPorts,
            DiscoveredUnmapped = discoveredUnmapped,
            SuggestedAppPort = PickSuggestedAppPort(mappedPorts, discoveredUnmapped),
        };
    }

    /// <summary>
    /// Pick the most-likely web-app port: prefer a mapped + listening
    /// non-reserved port (so Conductor can actually reach it), preferring
    /// well-known dev ports; otherwise fall back to any mapped non-reserved
    /// port, then any discovered listening non-reserved port.
    /// </summary>
    private static int? PickSuggestedAppPort(IReadOnlyList<MappedPort> mapped, IReadOnlyList<int> discoveredUnmapped)
    {
        var mappedListening = mapped.Where(m => m.Listening && !ReservedPorts.Contains(m.ContainerPort)).ToList();
        var preferred = mappedListening
            .Where(m => PreferredAppPorts.Contains(m.ContainerPort))
            .OrderBy(m => Array.IndexOf(PreferredAppPorts, m.ContainerPort))
            .FirstOrDefault();
        if (preferred is not null) return preferred.ContainerPort;
        if (mappedListening.Count > 0) return mappedListening[0].ContainerPort;

        var mappedNonReserved = mapped.Where(m => !ReservedPorts.Contains(m.ContainerPort)).ToList();
        if (mappedNonReserved.Count > 0) return mappedNonReserved[0].ContainerPort;

        var discoveredNonReserved = discoveredUnmapped.Where(p => !ReservedPorts.Contains(p)).ToList();
        if (discoveredNonReserved.Count > 0) return discoveredNonReserved[0];

        return null;
    }

    public Task<MappedPort> ExposePortAsync(Guid containerId, int containerPort, CancellationToken ct = default)
        => _containerService.ExposePortAsync(containerId, containerPort, ct);

    private async Task<HashSet<int>> ProbeListeningPortsAsync(Guid containerId, CancellationToken ct)
    {
        // Prefer `ss` (iproute2); fall back to `netstat` (net-tools); if
        // neither is present the command exits non-zero and we degrade to an
        // empty set. `-H` suppresses ss's header; `2>/dev/null` keeps stderr
        // out of the parsed output.
        const string script =
            "if command -v ss >/dev/null 2>&1; then ss -ltnH 2>/dev/null; " +
            "elif command -v netstat >/dev/null 2>&1; then netstat -ltn 2>/dev/null; " +
            "else exit 127; fi";

        try
        {
            var result = await _containerService.ExecAsync(containerId, script, ProbeTimeout, ct);
            if (result.ExitCode != 0)
            {
                _logger.LogDebug(
                    "Ports: listening-port probe in {ContainerId} exited {Exit}; degrading to mapped only.",
                    containerId, result.ExitCode);
                return new HashSet<int>();
            }
            return ParseListeningPorts(result.StdOut ?? string.Empty);
        }
        catch (InvalidOperationException ex)
        {
            // Container not running / no external id → no live ports.
            _logger.LogDebug(ex, "Ports: cannot probe {ContainerId}; degrading to mapped only.", containerId);
            return new HashSet<int>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ports: listening-port probe threw for {ContainerId}; degrading to mapped only.", containerId);
            return new HashSet<int>();
        }
    }

    /// <summary>
    /// Pure parser for <c>ss -ltn</c> / <c>netstat -ltn</c> output → the set
    /// of TCP ports in the LISTEN state. Handles both tool layouts and
    /// IPv4/IPv6 local-address formats. Split out for unit testing.
    /// </summary>
    public static HashSet<int> ParseListeningPorts(string output)
    {
        var ports = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(output)) return ports;

        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // Skip header rows from either tool.
            // ss header:     "State  Recv-Q  Send-Q  Local Address:Port ..."
            // netstat header:"Active Internet connections ..." / "Proto Recv-Q ..."
            if (line.StartsWith("State", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Proto", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Active", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var cols = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 4) continue;

            // Find the local address column. ss -ltnH columns:
            //   Recv-Q Send-Q LocalAddress:Port PeerAddress:Port
            // (state column suppressed by -H; some ss builds still print it,
            // so probe both the 3rd and 4th column).
            // netstat -ltn columns:
            //   Proto Recv-Q Send-Q LocalAddress:Port ForeignAddress State
            string? localAddr = null;
            if (line.StartsWith("tcp", StringComparison.OrdinalIgnoreCase))
            {
                // netstat: proto in col 0, local address in col 3.
                if (cols.Length >= 4) localAddr = cols[3];
            }
            else
            {
                // ss -ltnH: local address is the first "host:port" looking
                // column. Try col[3] (no -H) then col[2] (-H suppressed state).
                localAddr = ColumnWithPort(cols, 3) ?? ColumnWithPort(cols, 2) ?? ColumnWithPort(cols, 1);
            }

            if (localAddr is null) continue;
            var port = ExtractPort(localAddr);
            if (port is { } p && p is > 0 and <= 65535) ports.Add(p);
        }

        return ports;
    }

    private static string? ColumnWithPort(string[] cols, int index)
    {
        if (index < 0 || index >= cols.Length) return null;
        return cols[index].Contains(':') ? cols[index] : null;
    }

    /// <summary>
    /// Extract the port from a "Local Address:Port" token. Handles
    /// IPv4 (<c>0.0.0.0:8080</c>), IPv6 (<c>[::]:8080</c>, <c>*:8080</c>) and
    /// the <c>::ffff:0.0.0.0:8080</c> form by taking the segment after the
    /// final colon.
    /// </summary>
    private static int? ExtractPort(string localAddr)
    {
        var idx = localAddr.LastIndexOf(':');
        if (idx < 0 || idx == localAddr.Length - 1) return null;
        var portText = localAddr[(idx + 1)..];
        return int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            ? port
            : null;
    }
}
