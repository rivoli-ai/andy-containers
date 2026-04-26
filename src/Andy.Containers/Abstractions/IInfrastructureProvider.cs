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

    // Monitoring
    Task<ContainerStats> GetContainerStatsAsync(string externalId, CancellationToken ct = default);

    // Execution
    Task<ExecResult> ExecAsync(string externalId, string command, CancellationToken ct = default);
    Task<ExecResult> ExecAsync(string externalId, string command, TimeSpan timeout, CancellationToken ct = default);

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
}

public class ExecResult
{
    public int ExitCode { get; set; }
    public string? StdOut { get; set; }
    public string? StdErr { get; set; }
}

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
