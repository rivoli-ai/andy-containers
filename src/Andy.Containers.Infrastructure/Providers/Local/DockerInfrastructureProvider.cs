using System.Text.Json;
using Andy.Containers.Abstractions;
using Andy.Containers.Models;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using ContainerSpec = Andy.Containers.Abstractions.ContainerSpec;
using ContainerStatus = Andy.Containers.Models.ContainerStatus;

namespace Andy.Containers.Infrastructure.Providers.Local;

public class DockerInfrastructureProvider : IInfrastructureProvider
{
    private readonly DockerClient _client;
    private readonly ILogger<DockerInfrastructureProvider> _logger;

    public ProviderType Type => ProviderType.Docker;

    public DockerInfrastructureProvider(string? connectionConfig, ILogger<DockerInfrastructureProvider> logger)
    {
        _logger = logger;
        var endpoint = ResolveDockerEndpoint(connectionConfig);
        _logger.LogDebug("Using Docker endpoint: {Endpoint}", endpoint);
        _client = new DockerClientConfiguration(new Uri(endpoint)).CreateClient();
    }

    private static string ResolveDockerEndpoint(string? connectionConfig)
    {
        // Try explicit configuration first, but only if the socket actually exists
        if (!string.IsNullOrEmpty(connectionConfig))
        {
            try
            {
                var config = JsonSerializer.Deserialize<JsonElement>(connectionConfig);
                if (config.TryGetProperty("endpoint", out var ep))
                {
                    var configured = ep.GetString();
                    if (!string.IsNullOrEmpty(configured))
                    {
                        // For unix sockets, verify the file exists before committing to it
                        if (configured.StartsWith("unix://"))
                        {
                            var socketPath = configured["unix://".Length..];
                            if (File.Exists(socketPath))
                                return configured;
                            // Socket from config not found — fall through to auto-discovery
                        }
                        else
                        {
                            // TCP or other endpoints — trust the configuration
                            return configured;
                        }
                    }
                }
            }
            catch { }
        }

        // Auto-discover: default socket path
        const string defaultSocket = "/var/run/docker.sock";
        if (File.Exists(defaultSocket))
            return $"unix://{defaultSocket}";

        // macOS Docker Desktop places the socket under ~/.docker/run/
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(homeDir))
        {
            var dockerDesktopSocket = Path.Combine(homeDir, ".docker/run/docker.sock");
            if (File.Exists(dockerDesktopSocket))
                return $"unix://{dockerDesktopSocket}";
        }

        // Fallback to default even if not found — let HealthCheck report Unreachable
        return $"unix://{defaultSocket}";
    }

    public Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        return Task.FromResult(new ProviderCapabilities
        {
            Type = ProviderType.Docker,
            SupportedArchitectures = ["arm64", "amd64"],
            SupportedOperatingSystems = ["linux"],
            MaxCpuCores = 8,
            MaxMemoryMb = 16384,
            MaxDiskGb = 100,
            SupportsGpu = false,
            SupportsVolumeMount = true,
            SupportsPortForwarding = true,
            SupportsExec = true,
            SupportsStreaming = true,
            SupportsOfflineBuild = true
        });
    }

    public async Task<ProviderHealth> HealthCheckAsync(CancellationToken ct)
    {
        try
        {
            await _client.System.PingAsync(ct);
            return ProviderHealth.Healthy;
        }
        catch
        {
            return ProviderHealth.Unreachable;
        }
    }

    public async Task<ContainerProvisionResult> CreateContainerAsync(ContainerSpec spec, CancellationToken ct)
    {
        _logger.LogInformation("Creating Docker container {Name} from {Image}", spec.Name, spec.ImageReference);

        var containerName = spec.Name.ToLowerInvariant().Replace(' ', '-');

        // Remove any existing container with the same name (stopped or running)
        try
        {
            var existing = await _client.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["name"] = new Dictionary<string, bool> { [$"^/{containerName}$"] = true }
                }
            }, ct);

            foreach (var old in existing)
            {
                _logger.LogInformation("Removing existing container {Id} with name {Name}", old.ID[..12], containerName);
                await _client.Containers.RemoveContainerAsync(old.ID, new ContainerRemoveParameters { Force = true }, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check for existing container {Name}", containerName);
        }

        // Pull image if not present
        try
        {
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = spec.ImageReference },
                null,
                new Progress<JSONMessage>(m => _logger.LogDebug("Pull: {Status}", m.Status)),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to pull image {Image}, trying local", spec.ImageReference);
        }

        var portBindings = new Dictionary<string, IList<PortBinding>>();
        var exposedPorts = new Dictionary<string, EmptyStruct>();
        if (spec.PortMappings is not null)
        {
            foreach (var (container, host) in spec.PortMappings)
            {
                var key = $"{container}/tcp";
                exposedPorts[key] = default;
                portBindings[key] = new List<PortBinding> { new() { HostPort = host.ToString() } };
            }
        }

        // Build the command: use spec.Command if provided, otherwise default to
        // "sleep infinity" to keep the container alive as a dev environment.
        var cmd = new List<string>();
        if (!string.IsNullOrEmpty(spec.Command))
        {
            cmd.Add(spec.Command);
            if (spec.Arguments is not null)
                cmd.AddRange(spec.Arguments);
        }
        else
        {
            cmd.AddRange(["sleep", "infinity"]);
        }

        var response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = spec.ImageReference,
            Name = containerName,
            Cmd = cmd,
            Env = spec.EnvironmentVariables?.Select(kv => $"{kv.Key}={kv.Value}").ToList(),
            ExposedPorts = exposedPorts,
            Labels = spec.Labels,
            HostConfig = new HostConfig
            {
                PortBindings = portBindings,
                Memory = (long)(spec.Resources?.MemoryMb ?? 4096) * 1024 * 1024,
                NanoCPUs = (long)((spec.Resources?.CpuCores ?? 2) * 1e9)
            }
        }, ct);

        await _client.Containers.StartContainerAsync(response.ID, new ContainerStartParameters(), ct);

        _logger.LogInformation("Docker container {Id} created and started", response.ID);

        return new ContainerProvisionResult
        {
            ExternalId = response.ID,
            Status = ContainerStatus.Running,
            ConnectionInfo = await GetConnectionInfoAsync(response.ID, ct)
        };
    }

    public async Task StartContainerAsync(string externalId, CancellationToken ct)
    {
        await _client.Containers.StartContainerAsync(externalId, new ContainerStartParameters(), ct);
    }

    public async Task StopContainerAsync(string externalId, CancellationToken ct)
    {
        await _client.Containers.StopContainerAsync(externalId, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, ct);
    }

    public async Task DestroyContainerAsync(string externalId, CancellationToken ct)
    {
        await _client.Containers.RemoveContainerAsync(externalId, new ContainerRemoveParameters { Force = true }, ct);
    }

    public async Task<ContainerRuntimeInfo> GetContainerInfoAsync(string externalId, CancellationToken ct)
    {
        var inspect = await _client.Containers.InspectContainerAsync(externalId, ct);
        return new ContainerRuntimeInfo
        {
            ExternalId = externalId,
            Status = inspect.State.Running ? ContainerStatus.Running : ContainerStatus.Stopped,
            StartedAt = inspect.State.StartedAt != default ? DateTime.Parse(inspect.State.StartedAt) : null,
            IpAddress = inspect.NetworkSettings?.IPAddress
        };
    }

    public async Task<ContainerProvisionResult> ResizeContainerAsync(string externalId, ResourceSpec resources, CancellationToken ct)
    {
        await _client.Containers.UpdateContainerAsync(externalId, new ContainerUpdateParameters
        {
            Memory = (long)resources.MemoryMb * 1024 * 1024,
            NanoCPUs = (long)(resources.CpuCores * 1e9)
        }, ct);
        return new ContainerProvisionResult { ExternalId = externalId, Status = ContainerStatus.Running };
    }

    public async Task<ConnectionInfo> GetConnectionInfoAsync(string externalId, CancellationToken ct)
    {
        var inspect = await _client.Containers.InspectContainerAsync(externalId, ct);
        var ports = new Dictionary<int, int>();
        if (inspect.NetworkSettings?.Ports is not null)
        {
            foreach (var (key, bindings) in inspect.NetworkSettings.Ports)
            {
                if (bindings is not null && bindings.Count > 0 && int.TryParse(key.Split('/')[0], out var containerPort))
                {
                    if (int.TryParse(bindings[0].HostPort, out var hostPort))
                        ports[containerPort] = hostPort;
                }
            }
        }

        return new ConnectionInfo
        {
            IpAddress = inspect.NetworkSettings?.IPAddress,
            PortMappings = ports,
            IdeEndpoint = ports.TryGetValue(8080, out var idePort) ? $"https://localhost:{idePort}" : null,
            VncEndpoint = ports.TryGetValue(6080, out var vncPort) ? $"https://localhost:{vncPort}" : null
        };
    }

    public async Task<ContainerStats> GetContainerStatsAsync(string externalId, CancellationToken ct)
    {
        var inspect = await _client.Containers.InspectContainerAsync(externalId, ct);

        // Get a single stats snapshot (stream: false)
        var statsResponse = new ContainerStatsResponse();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        await _client.Containers.GetContainerStatsAsync(externalId,
            new ContainerStatsParameters { Stream = false },
            new Progress<ContainerStatsResponse>(s => statsResponse = s),
            cts.Token);

        // CPU %
        double cpuPercent = 0;
        if (statsResponse.CPUStats?.CPUUsage != null && statsResponse.PreCPUStats?.CPUUsage != null)
        {
            var cpuDelta = (double)(statsResponse.CPUStats.CPUUsage.TotalUsage - statsResponse.PreCPUStats.CPUUsage.TotalUsage);
            var systemDelta = (double)(statsResponse.CPUStats.SystemUsage - statsResponse.PreCPUStats.SystemUsage);
            var numCpus = statsResponse.CPUStats.OnlineCPUs > 0
                ? statsResponse.CPUStats.OnlineCPUs
                : (uint)(statsResponse.CPUStats.CPUUsage.PercpuUsage?.Count ?? 1);
            if (systemDelta > 0 && cpuDelta >= 0)
                cpuPercent = cpuDelta / systemDelta * numCpus * 100.0;
        }

        // Memory
        long memUsage = (long)(statsResponse.MemoryStats?.Usage ?? 0);
        long memLimit = (long)(statsResponse.MemoryStats?.Limit ?? 0);
        double memPercent = memLimit > 0 ? (double)memUsage / memLimit * 100.0 : 0;

        // Disk: use container's SizeRootFs from inspect if available
        long diskUsage = inspect.SizeRootFs ?? 0;
        long diskLimit = 0;

        return new ContainerStats
        {
            CpuPercent = Math.Round(cpuPercent, 1),
            MemoryUsageBytes = memUsage,
            MemoryLimitBytes = memLimit,
            MemoryPercent = Math.Round(memPercent, 1),
            DiskUsageBytes = diskUsage,
            DiskLimitBytes = diskLimit,
            DiskPercent = diskLimit > 0 ? Math.Round((double)diskUsage / diskLimit * 100.0, 1) : 0,
        };
    }

    public async Task<ExecResult> ExecAsync(string externalId, string command, CancellationToken ct)
    {
        return await ExecAsync(externalId, command, TimeSpan.FromSeconds(30), ct);
    }

    public async Task<ExecResult> ExecAsync(string externalId, string command, TimeSpan timeout, CancellationToken ct)
    {
        var exec = await _client.Exec.ExecCreateContainerAsync(externalId, new ContainerExecCreateParameters
        {
            Cmd = ["sh", "-c", command],
            AttachStdout = true,
            AttachStderr = true
        }, ct);

        using var stream = await _client.Exec.StartAndAttachContainerExecAsync(exec.ID, false, ct);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(ct);

        var inspect = await _client.Exec.InspectContainerExecAsync(exec.ID, ct);

        return new ExecResult
        {
            ExitCode = (int)inspect.ExitCode,
            StdOut = stdout,
            StdErr = stderr
        };
    }
}
