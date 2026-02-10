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
        var endpoint = "unix:///var/run/docker.sock";
        if (!string.IsNullOrEmpty(connectionConfig))
        {
            try
            {
                var config = JsonSerializer.Deserialize<JsonElement>(connectionConfig);
                if (config.TryGetProperty("endpoint", out var ep))
                    endpoint = ep.GetString() ?? endpoint;
            }
            catch { }
        }

        _client = new DockerClientConfiguration(new Uri(endpoint)).CreateClient();
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

        var response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = spec.ImageReference,
            Name = spec.Name.ToLowerInvariant().Replace(' ', '-'),
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
