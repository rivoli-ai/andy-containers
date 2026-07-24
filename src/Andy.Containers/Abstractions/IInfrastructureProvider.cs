using Andy.Containers.Models;

namespace Andy.Containers.Abstractions;

/// <summary>
/// Abstraction over a compute backend that can provision and manage containers.
/// Each infrastructure type (Docker, Apple Containers, Azure, SSH, etc.) provides
/// its own implementation.
/// </summary>
public interface IInfrastructureProvider
{
    ProviderType Type { get; }

    Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct = default);
    Task<ProviderHealth> HealthCheckAsync(CancellationToken ct = default);

    // Container lifecycle
    Task<ContainerProvisionResult> CreateContainerAsync(ContainerSpec spec, CancellationToken ct = default);
    Task StartContainerAsync(string externalId, CancellationToken ct = default);
    Task StopContainerAsync(string externalId, CancellationToken ct = default);
    Task DestroyContainerAsync(string externalId, CancellationToken ct = default);
    Task<ContainerRuntimeInfo> GetContainerInfoAsync(string externalId, CancellationToken ct = default);

    // Resource management
    Task<ContainerProvisionResult> ResizeContainerAsync(string externalId, ResourceSpec resources, CancellationToken ct = default);

    // Connectivity
    Task<ConnectionInfo> GetConnectionInfoAsync(string externalId, CancellationToken ct = default);

    /// <summary>
    /// F6.4 (rivoli-ai/conductor#1943). Publishes a container TCP port to a
    /// host (loopback) port so Conductor can preview a web app the agent
    /// started, returning the resulting (containerPort → hostPort) mapping.
    ///
    /// Docker can only publish ports at create-time, so a provider that
    /// cannot add a mapping to an already-running container MUST throw
    /// <see cref="NotSupportedException"/> with an explanatory message — the
    /// API surfaces it as a 400 (same pattern as live resource resize). The
    /// common case (the app's port was published when the container was
    /// created) is covered without this method: it surfaces through
    /// <see cref="GetConnectionInfoAsync"/>'s <c>PortMappings</c>.
    ///
    /// The default throws — only providers that genuinely support live port
    /// addition override it. Honours decision #17: no new Docker-Engine verb
    /// (Docker's answer here is always "recreate", hence the throw).
    /// </summary>
    Task<MappedPort> ExposePortAsync(string externalId, int containerPort, CancellationToken ct = default)
        => throw new NotSupportedException(
            "This provider cannot add a port mapping to a running container. " +
            "Publish the port when the container is created instead.");

    // Monitoring
    Task<ContainerStats> GetContainerStatsAsync(string externalId, CancellationToken ct = default);

    // Execution
    Task<ExecResult> ExecAsync(string externalId, string command, CancellationToken ct = default);
    Task<ExecResult> ExecAsync(string externalId, string command, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// F4.1 (rivoli-ai/conductor#1934). Streaming exec: same exec/attach
    /// surface as <see cref="ExecAsync(string, string, TimeSpan, CancellationToken)"/>
    /// (decision #17 — no new Docker-Engine verb), but each stdout/stderr
    /// line is delivered to <paramref name="onLine"/> as produced. The
    /// default delegates to the buffered overload and replays the final
    /// output line-by-line — correct for providers that can't stream
    /// incrementally; Docker overrides for a true live tail.
    /// </summary>
    async Task<ExecResult> ExecStreamingAsync(
        string externalId,
        string command,
        TimeSpan timeout,
        Action<ExecOutputChunk> onLine,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        var result = await ExecAsync(externalId, command, timeout, ct);
        Replay(result.StdOut, ExecStreamKind.Stdout, onLine);
        Replay(result.StdErr, ExecStreamKind.Stderr, onLine);
        return result;

        static void Replay(string? text, ExecStreamKind kind, Action<ExecOutputChunk> sink)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Length > 0) sink(new ExecOutputChunk(kind, line));
            }
        }
    }

    /// <summary>
    /// Asynchronous-callback variant of streaming exec. This keeps the
    /// existing callback contract source-compatible while allowing HTTP
    /// transports to await each write and apply backpressure.
    /// Providers with a native streaming implementation should override
    /// this overload; the default adapts the synchronous callback.
    /// </summary>
    Task<ExecResult> ExecStreamingAsync(
        string externalId,
        string command,
        TimeSpan timeout,
        Func<ExecOutputChunk, CancellationToken, ValueTask> onLine,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        return ExecStreamingAsync(
            externalId,
            command,
            timeout,
            chunk => onLine(chunk, ct).AsTask().GetAwaiter().GetResult(),
            ct);
    }

    /// <summary>
    /// Returns the set of container externalIds currently known to the
    /// provider, or <c>null</c> if this provider does not support bulk
    /// enumeration. Used by the startup reconciler (conductor #840) to
    /// detect rows whose containers were removed out-of-band (host
    /// reboot, manual <c>docker rm -f</c>, etc.) without paying the
    /// per-row cost of <see cref="GetContainerInfoAsync"/>.
    ///
    /// Cloud providers (AWS, Azure, GCP, Fly, etc.) typically return
    /// <c>null</c> — the existing periodic <c>ContainerStatusSyncWorker</c>
    /// covers them via per-row probes. Local providers (Docker, Apple
    /// Containers) override this to issue a single CLI call.
    /// </summary>
    Task<HashSet<string>?> ListExternalIdsAsync(CancellationToken ct = default)
        => Task.FromResult<HashSet<string>?>(null);

    /// <summary>
    /// Opens an interactive PTY-backed exec session inside the
    /// container. Conductor #875 PR 1.
    ///
    /// The returned <see cref="IInteractiveExecSession"/> exposes the
    /// inner shell's stdin / stdout as a single multiplexed stream
    /// plus a `Resize(cols, rows)` method that propagates SIGWINCH
    /// down to the inner shell. This is what makes tmux / claude /
    /// vim render correctly on window resize — the docker daemon (or
    /// equivalent) owns the PTY and routes the size change through
    /// the proper OS-level path.
    ///
    /// Returns <c>null</c> when the provider does not support
    /// daemon-side PTY allocation. Callers fall back to a script-based
    /// host-side PTY (the legacy path used by all providers before
    /// this method existed).
    /// </summary>
    Task<IInteractiveExecSession?> OpenInteractiveExecAsync(
        string externalId,
        string[] command,
        string user,
        string workingDirectory,
        int cols,
        int rows,
        CancellationToken ct = default)
        => Task.FromResult<IInteractiveExecSession?>(null);
}

/// <summary>
/// Interactive PTY-backed exec session. Read / write its single
/// bidirectional byte stream and resize via <see cref="ResizeAsync"/>.
/// Implementations route resize to the daemon's native PTY-size API
/// so the inner shell receives SIGWINCH the canonical way.
///
/// Conductor #875 PR 1. Replaces the script-wrapped Process chain
/// for providers that support daemon-side PTY allocation.
/// </summary>
public interface IInteractiveExecSession : IAsyncDisposable
{
    /// <summary>
    /// Reads a chunk of bytes from the inner shell's stdout/stderr.
    /// Returns 0 when the inner shell exits.
    /// </summary>
    Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct);

    /// <summary>
    /// Writes a chunk of bytes to the inner shell's stdin.
    /// </summary>
    Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct);

    /// <summary>
    /// Asks the daemon to resize the inner shell's PTY. The shell
    /// receives SIGWINCH and re-renders at the new size.
    /// </summary>
    Task ResizeAsync(int cols, int rows, CancellationToken ct);
}

public class ProviderCapabilities
{
    public required ProviderType Type { get; set; }
    public required string[] SupportedArchitectures { get; set; }
    public required string[] SupportedOperatingSystems { get; set; }
    public int MaxCpuCores { get; set; }
    public int MaxMemoryMb { get; set; }
    public int MaxDiskGb { get; set; }
    public bool SupportsGpu { get; set; }
    public GpuCapability[]? GpuCapabilities { get; set; }
    public bool SupportsVolumeMount { get; set; }
    public bool SupportsPortForwarding { get; set; }
    public bool SupportsExec { get; set; }
    public bool SupportsStreaming { get; set; }
    public bool SupportsOfflineBuild { get; set; }
}

public class GpuCapability
{
    public required string Vendor { get; set; }
    public required string Model { get; set; }
    public int MemoryMb { get; set; }
    public int Count { get; set; }
    public bool IsAvailable { get; set; }
}

public class ContainerSpec
{
    public required string ImageReference { get; set; }
    public required string Name { get; set; }
    public ResourceSpec? Resources { get; set; }
    public GpuSpec? Gpu { get; set; }
    public Dictionary<string, string>? EnvironmentVariables { get; set; }
    public Dictionary<int, int>? PortMappings { get; set; }
    public VolumeMount[]? VolumeMounts { get; set; }
    public string? Command { get; set; }
    public string[]? Arguments { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
}

public class ResourceSpec
{
    public double CpuCores { get; set; } = 2;
    public int MemoryMb { get; set; } = 4096;
    public int DiskGb { get; set; } = 20;
}

public class GpuSpec
{
    public bool Required { get; set; }
    public string? Vendor { get; set; }
    public int? MinMemoryMb { get; set; }
    public int Count { get; set; } = 1;
}

public class VolumeMount
{
    public required string HostPath { get; set; }
    public required string ContainerPath { get; set; }
    public bool ReadOnly { get; set; }
}

public class ContainerProvisionResult
{
    public required string ExternalId { get; set; }
    public ContainerStatus Status { get; set; }
    public ConnectionInfo? ConnectionInfo { get; set; }
    public string? ErrorMessage { get; set; }
}

public class ContainerRuntimeInfo
{
    public required string ExternalId { get; set; }
    public ContainerStatus Status { get; set; }
    public ResourceSpec? AllocatedResources { get; set; }
    public DateTime? StartedAt { get; set; }
    public string? IpAddress { get; set; }
    public Dictionary<int, int>? PortMappings { get; set; }
}

public class ConnectionInfo
{
    public string? IpAddress { get; set; }
    public Dictionary<int, int>? PortMappings { get; set; }
    public string? IdeEndpoint { get; set; }
    public string? VncEndpoint { get; set; }
    public string? SshEndpoint { get; set; }
    public string? AgentEndpoint { get; set; }

    /// <summary>
    /// F6.4 (rivoli-ai/conductor#1943). Loopback URLs for web apps the run
    /// published to a host port (the non-reserved <see cref="PortMappings"/>
    /// entries, i.e. excluding IDE/VNC/SSH/agent), so Conductor can preview
    /// them in an embedded browser through the UnifiedProxy. Each is
    /// <c>http://localhost:&lt;hostPort&gt;</c>. Empty when the run exposed
    /// no web port.
    /// </summary>
    public IReadOnlyList<string> WebAppEndpoints { get; set; } = new List<string>();
}

public class ExecResult
{
    public int ExitCode { get; set; }
    public string? StdOut { get; set; }
    public string? StdErr { get; set; }
}

/// <summary>Which stream a streamed exec chunk came from. F4.1 (#1934).</summary>
public enum ExecStreamKind
{
    Stdout,
    Stderr,
}

/// <summary>
/// One incremental line of exec output surfaced by
/// <see cref="IContainerService.ExecStreamingAsync"/>. F4.1 (#1934).
/// </summary>
public readonly record struct ExecOutputChunk(ExecStreamKind Stream, string Line);

public class ContainerStats
{
    public double CpuPercent { get; set; }
    public long MemoryUsageBytes { get; set; }
    public long MemoryLimitBytes { get; set; }
    public double MemoryPercent { get; set; }
    public long DiskUsageBytes { get; set; }
    public long DiskLimitBytes { get; set; }
    public double DiskPercent { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
