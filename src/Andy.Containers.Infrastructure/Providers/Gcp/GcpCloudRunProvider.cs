using System.Text.Json;
using Andy.Containers.Abstractions;
using Andy.Containers.Models;
using Google.Cloud.Run.V2;
using Microsoft.Extensions.Logging;
using ContainerSpec = Andy.Containers.Abstractions.ContainerSpec;
using ContainerStatus = Andy.Containers.Models.ContainerStatus;
using Task = System.Threading.Tasks.Task;

namespace Andy.Containers.Infrastructure.Providers.Gcp;

public class GcpCloudRunProvider : IInfrastructureProvider
{
    private readonly ILogger<GcpCloudRunProvider> _logger;
    private readonly string _projectId;
    private readonly string _region;
    private readonly ServicesClient _servicesClient;
    private readonly RevisionsClient _revisionsClient;

    public ProviderType Type => ProviderType.GcpCloudRun;

    public GcpCloudRunProvider(string? connectionConfig, ILogger<GcpCloudRunProvider> logger)
    {
        _logger = logger;
        _projectId = "";
        _region = "us-central1";

        if (!string.IsNullOrEmpty(connectionConfig))
        {
            try
            {
                var config = JsonSerializer.Deserialize<JsonElement>(connectionConfig);
                if (config.TryGetProperty("projectId", out var pid))
                    _projectId = pid.GetString() ?? "";
                if (config.TryGetProperty("region", out var region))
                    _region = region.GetString() ?? _region;
            }
            catch { }
        }

        _servicesClient = ServicesClient.Create();
        _revisionsClient = RevisionsClient.Create();
    }

    public Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        return Task.FromResult(new ProviderCapabilities
        {
            Type = ProviderType.GcpCloudRun,
            SupportedArchitectures = ["amd64"],
            SupportedOperatingSystems = ["linux"],
            MaxCpuCores = 8,
            MaxMemoryMb = 32768,
            MaxDiskGb = 0,
            SupportsGpu = true,
            GpuCapabilities =
            [
                new GpuCapability { Vendor = "NVIDIA", Model = "L4", MemoryMb = 24576, Count = 1, IsAvailable = true }
            ],
            SupportsVolumeMount = false,
            SupportsPortForwarding = true,
            SupportsExec = true,    // via SSH fallback
            SupportsStreaming = false,
            SupportsOfflineBuild = false
        });
    }

    public async Task<ProviderHealth> HealthCheckAsync(CancellationToken ct)
    {
        try
        {
            var parent = $"projects/{_projectId}/locations/{_region}";
            var request = new ListServicesRequest { Parent = parent };
            var services = _servicesClient.ListServicesAsync(request);
            // Just try to enumerate one to verify connectivity
            await using var enumerator = services.GetAsyncEnumerator(ct);
            await enumerator.MoveNextAsync();
            return ProviderHealth.Healthy;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GCP Cloud Run health check failed");
            return ProviderHealth.Unreachable;
        }
    }

    public async Task<ContainerProvisionResult> CreateContainerAsync(ContainerSpec spec, CancellationToken ct)
    {
        _logger.LogInformation("Creating GCP Cloud Run service {Name} from {Image}", spec.Name, spec.ImageReference);

        var serviceName = $"andy-{Guid.NewGuid().ToString("N")[..12]}";
        var resources = spec.Resources ?? new ResourceSpec();

        var container = new Google.Cloud.Run.V2.Container
        {
            Image = spec.ImageReference,
            Resources = new ResourceRequirements
            {
                Limits =
                {
                    ["cpu"] = resources.CpuCores.ToString("F0"),
                    ["memory"] = $"{resources.MemoryMb}Mi"
                }
            }
        };

        if (spec.PortMappings is not null)
        {
            var firstPort = spec.PortMappings.Keys.FirstOrDefault();
            if (firstPort > 0)
            {
                container.Ports.Add(new ContainerPort { ContainerPort_ = firstPort });
            }
        }

        if (spec.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in spec.EnvironmentVariables)
            {
                container.Env.Add(new EnvVar { Name = key, Value = value });
            }
        }

        if (!string.IsNullOrEmpty(spec.Command))
        {
            container.Command.Add(spec.Command);
            if (spec.Arguments is not null)
            {
                foreach (var arg in spec.Arguments)
                    container.Args.Add(arg);
            }
        }

        var service = new Service
        {
            Template = new RevisionTemplate
            {
                Containers = { container },
                Scaling = new RevisionScaling
                {
                    MinInstanceCount = 1,
                    MaxInstanceCount = 1
                }
            }
        };

        var parent = $"projects/{_projectId}/locations/{_region}";
        var operation = await _servicesClient.CreateServiceAsync(parent, service, serviceName, ct);
        var result = await operation.PollUntilCompletedAsync();

        _logger.LogInformation("GCP Cloud Run service {Name} created: {Uri}", serviceName, result.Result.Uri);

        return new ContainerProvisionResult
        {
            ExternalId = serviceName,
            Status = ContainerStatus.Running,
            ConnectionInfo = new ConnectionInfo
            {
                IpAddress = result.Result.Uri,
                IdeEndpoint = result.Result.Uri
            }
        };
    }

    public Task StartContainerAsync(string externalId, CancellationToken ct)
    {
        // Cloud Run services are always "running" when they have min instances > 0
        // To start, set min instances back to 1
        return UpdateMinInstancesAsync(externalId, 1, ct);
    }

    public Task StopContainerAsync(string externalId, CancellationToken ct)
    {
        // Set min instances to 0 to effectively "stop" the service
        return UpdateMinInstancesAsync(externalId, 0, ct);
    }

    public async Task DestroyContainerAsync(string externalId, CancellationToken ct)
    {
        var name = $"projects/{_projectId}/locations/{_region}/services/{externalId}";
        var operation = await _servicesClient.DeleteServiceAsync(name, ct);
        await operation.PollUntilCompletedAsync();
        _logger.LogInformation("GCP Cloud Run service {Name} destroyed", externalId);
    }

    public async Task<ContainerRuntimeInfo> GetContainerInfoAsync(string externalId, CancellationToken ct)
    {
        var name = $"projects/{_projectId}/locations/{_region}/services/{externalId}";
        var service = await _servicesClient.GetServiceAsync(name, ct);

        var isReady = service.TerminalCondition?.State == Condition.Types.State.ConditionSucceeded;

        return new ContainerRuntimeInfo
        {
            ExternalId = externalId,
            Status = isReady ? ContainerStatus.Running : ContainerStatus.Pending,
            IpAddress = service.Uri
        };
    }

    public Task<ContainerProvisionResult> ResizeContainerAsync(string externalId, ResourceSpec resources, CancellationToken ct)
    {
        throw new NotSupportedException("GCP Cloud Run does not support in-place resize. Deploy a new revision with updated resources.");
    }

    public async Task<ConnectionInfo> GetConnectionInfoAsync(string externalId, CancellationToken ct)
    {
        var name = $"projects/{_projectId}/locations/{_region}/services/{externalId}";
        var service = await _servicesClient.GetServiceAsync(name, ct);

        return new ConnectionInfo
        {
            IpAddress = service.Uri,
            IdeEndpoint = service.Uri
        };
    }

    public Task<ExecResult> ExecAsync(string externalId, string command, CancellationToken ct)
    {
        return ExecAsync(externalId, command, TimeSpan.FromSeconds(30), ct);
    }

    public Task<ExecResult> ExecAsync(string externalId, string command, TimeSpan timeout, CancellationToken ct)
    {
        // GCP Cloud Run does not have a native exec API.
        // SSH fallback is needed: the container image must include an SSH server,
        // and the provider connects via SSH.NET to execute commands.
        throw new NotSupportedException(
            "GCP Cloud Run does not support native exec. " +
            "Configure SSH in the container image and use the SSH endpoint for command execution.");
    }

    private async Task UpdateMinInstancesAsync(string externalId, int minInstances, CancellationToken ct)
    {
        var name = $"projects/{_projectId}/locations/{_region}/services/{externalId}";
        var service = await _servicesClient.GetServiceAsync(name, ct);

        service.Template.Scaling.MinInstanceCount = minInstances;

        var operation = await _servicesClient.UpdateServiceAsync(service, ct);
        await operation.PollUntilCompletedAsync();
    }
}
