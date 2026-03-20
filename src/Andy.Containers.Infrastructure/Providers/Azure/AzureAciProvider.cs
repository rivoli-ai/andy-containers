using System.Text.Json;
using Andy.Containers.Abstractions;
using Andy.Containers.Models;
using global::Azure.Identity;
using global::Azure.ResourceManager;
using global::Azure.ResourceManager.ContainerInstance;
using global::Azure.ResourceManager.ContainerInstance.Models;
using global::Azure.ResourceManager.Resources;
using Microsoft.Extensions.Logging;
using AzureLocation = global::Azure.Core.AzureLocation;
using AzureResourceIdentifier = global::Azure.Core.ResourceIdentifier;
using AzureWaitUntil = global::Azure.WaitUntil;
using ContainerSpec = Andy.Containers.Abstractions.ContainerSpec;
using ContainerStatus = Andy.Containers.Models.ContainerStatus;

namespace Andy.Containers.Infrastructure.Providers.Azure;

public class AzureAciProvider : IInfrastructureProvider
{
    private readonly ILogger<AzureAciProvider> _logger;
    private readonly string _subscriptionId;
    private readonly string _resourceGroup;
    private readonly string _region;
    private readonly string? _registryServer;
    private readonly string? _registryUsername;
    private readonly string? _registryPassword;
    private readonly ArmClient _armClient;

    public ProviderType Type => ProviderType.AzureAci;

    public AzureAciProvider(string? connectionConfig, ILogger<AzureAciProvider> logger)
    {
        _logger = logger;
        _subscriptionId = "";
        _resourceGroup = "andy-containers";
        _region = "eastus";

        if (!string.IsNullOrEmpty(connectionConfig))
        {
            try
            {
                var config = JsonSerializer.Deserialize<JsonElement>(connectionConfig);
                if (config.TryGetProperty("subscriptionId", out var sub))
                    _subscriptionId = sub.GetString() ?? "";
                if (config.TryGetProperty("resourceGroup", out var rg))
                    _resourceGroup = rg.GetString() ?? _resourceGroup;
                if (config.TryGetProperty("region", out var region))
                    _region = region.GetString() ?? _region;
                if (config.TryGetProperty("registryServer", out var rs))
                    _registryServer = rs.GetString();
                if (config.TryGetProperty("registryUsername", out var ru))
                    _registryUsername = ru.GetString();
                if (config.TryGetProperty("registryPassword", out var rp))
                    _registryPassword = rp.GetString();
            }
            catch { }
        }

        _armClient = new ArmClient(new DefaultAzureCredential());
    }

    public Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        return Task.FromResult(new ProviderCapabilities
        {
            Type = ProviderType.AzureAci,
            SupportedArchitectures = ["amd64", "arm64"],
            SupportedOperatingSystems = ["linux"],
            MaxCpuCores = 4,
            MaxMemoryMb = 16384,
            MaxDiskGb = 50,
            SupportsGpu = true,
            GpuCapabilities =
            [
                new GpuCapability { Vendor = "NVIDIA", Model = "T4", MemoryMb = 16384, Count = 1, IsAvailable = true },
                new GpuCapability { Vendor = "NVIDIA", Model = "V100", MemoryMb = 16384, Count = 1, IsAvailable = true }
            ],
            SupportsVolumeMount = false,
            SupportsPortForwarding = true,
            SupportsExec = true,
            SupportsStreaming = false,
            SupportsOfflineBuild = false
        });
    }

    public async Task<ProviderHealth> HealthCheckAsync(CancellationToken ct)
    {
        try
        {
            var subscription = _armClient.GetSubscriptionResource(
                new AzureResourceIdentifier($"/subscriptions/{_subscriptionId}"));
            var rg = await subscription.GetResourceGroupAsync(_resourceGroup, ct);
            return rg.Value is not null ? ProviderHealth.Healthy : ProviderHealth.Degraded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure ACI health check failed");
            return ProviderHealth.Unreachable;
        }
    }

    public async Task<ContainerProvisionResult> CreateContainerAsync(ContainerSpec spec, CancellationToken ct)
    {
        _logger.LogInformation("Creating Azure ACI container {Name} from {Image}", spec.Name, spec.ImageReference);

        var containerGroupName = $"andy-{Guid.NewGuid().ToString("N")[..12]}";
        var resources = spec.Resources ?? new ResourceSpec();

        var containerResource = new ContainerInstanceContainer(
            spec.Name.ToLowerInvariant().Replace(' ', '-'),
            spec.ImageReference,
            new ContainerResourceRequirements(
                new ContainerResourceRequestsContent(resources.MemoryMb / 1024.0, resources.CpuCores)));

        if (spec.PortMappings is not null)
        {
            foreach (var (containerPort, _) in spec.PortMappings)
            {
                containerResource.Ports.Add(new ContainerPort(containerPort));
            }
        }

        // Expose SSH port when enabled
        if (spec.SshEnabled)
        {
            containerResource.Ports.Add(new ContainerPort(spec.SshPort));
        }

        if (spec.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in spec.EnvironmentVariables)
            {
                containerResource.EnvironmentVariables.Add(
                    new ContainerEnvironmentVariable(key) { Value = value });
            }
        }

        if (!string.IsNullOrEmpty(spec.Command))
        {
            containerResource.Command.Add(spec.Command);
            if (spec.Arguments is not null)
            {
                foreach (var arg in spec.Arguments)
                    containerResource.Command.Add(arg);
            }
        }

        var groupPorts = spec.PortMappings?.Select(p => new ContainerGroupPort(p.Key) { Protocol = ContainerGroupNetworkProtocol.Tcp }).ToList()
            ?? [new ContainerGroupPort(80) { Protocol = ContainerGroupNetworkProtocol.Tcp }];

        if (spec.SshEnabled)
        {
            groupPorts.Add(new ContainerGroupPort(spec.SshPort) { Protocol = ContainerGroupNetworkProtocol.Tcp });
        }

        var containerGroupData = new ContainerGroupData(
            new AzureLocation(_region),
            [containerResource],
            ContainerInstanceOperatingSystemType.Linux)
        {
            IPAddress = new ContainerGroupIPAddress(groupPorts, ContainerGroupIPAddressType.Public)
        };

        // Add registry credentials if configured
        if (!string.IsNullOrEmpty(_registryServer) && !string.IsNullOrEmpty(_registryUsername))
        {
            containerGroupData.ImageRegistryCredentials.Add(
                new ContainerGroupImageRegistryCredential(_registryServer)
                {
                    Username = _registryUsername,
                    Password = _registryPassword
                });
        }

        var subscription = _armClient.GetSubscriptionResource(
            new AzureResourceIdentifier($"/subscriptions/{_subscriptionId}"));
        var rg = (await subscription.GetResourceGroupAsync(_resourceGroup, ct)).Value;
        var containerGroups = rg.GetContainerGroups();

        var operation = await containerGroups.CreateOrUpdateAsync(
            AzureWaitUntil.Completed, containerGroupName, containerGroupData, ct);

        _logger.LogInformation("Azure ACI container group {Name} created", containerGroupName);

        return new ContainerProvisionResult
        {
            ExternalId = containerGroupName,
            Status = ContainerStatus.Running,
            ConnectionInfo = GetConnectionInfoFromGroup(operation.Value)
        };
    }

    public async Task StartContainerAsync(string externalId, CancellationToken ct)
    {
        var group = await GetContainerGroupAsync(externalId, ct);
        await group.StartAsync(AzureWaitUntil.Completed, ct);
    }

    public async Task StopContainerAsync(string externalId, CancellationToken ct)
    {
        var group = await GetContainerGroupAsync(externalId, ct);
        await group.StopAsync(ct);
    }

    public async Task DestroyContainerAsync(string externalId, CancellationToken ct)
    {
        var group = await GetContainerGroupAsync(externalId, ct);
        await group.DeleteAsync(AzureWaitUntil.Completed, ct);
        _logger.LogInformation("Azure ACI container group {Name} destroyed", externalId);
    }

    public async Task<ContainerRuntimeInfo> GetContainerInfoAsync(string externalId, CancellationToken ct)
    {
        var group = await GetContainerGroupAsync(externalId, ct);
        var state = group.Data.InstanceView?.State;
        var status = state switch
        {
            "Running" => ContainerStatus.Running,
            "Stopped" or "Terminated" => ContainerStatus.Stopped,
            _ => ContainerStatus.Pending
        };

        return new ContainerRuntimeInfo
        {
            ExternalId = externalId,
            Status = status,
            IpAddress = group.Data.IPAddress?.IP?.ToString()
        };
    }

    public Task<ContainerProvisionResult> ResizeContainerAsync(string externalId, ResourceSpec resources, CancellationToken ct)
    {
        throw new NotSupportedException("Azure ACI does not support in-place container resize. Recreate the container with new resource specs.");
    }

    public async Task<ConnectionInfo> GetConnectionInfoAsync(string externalId, CancellationToken ct)
    {
        var group = await GetContainerGroupAsync(externalId, ct);
        return GetConnectionInfoFromGroup(group);
    }

    public async Task<ExecResult> ExecAsync(string externalId, string command, CancellationToken ct)
    {
        return await ExecAsync(externalId, command, TimeSpan.FromSeconds(30), ct);
    }

    public async Task<ExecResult> ExecAsync(string externalId, string command, TimeSpan timeout, CancellationToken ct)
    {
        var group = await GetContainerGroupAsync(externalId, ct);
        var containerName = group.Data.Containers.FirstOrDefault()?.Name
            ?? throw new InvalidOperationException("No containers found in group");

        var execRequest = new ContainerExecContent
        {
            Command = "/bin/sh",
            TerminalSize = new ContainerExecRequestTerminalSize { Rows = 24, Cols = 80 }
        };

        var execResult = await group.ExecuteContainerCommandAsync(containerName, execRequest, ct);

        _logger.LogInformation("ACI exec initiated for {Container} in group {Group}, WebSocket: {Url}",
            containerName, externalId, execResult.Value.WebSocketUri);

        // ACI exec is WebSocket-based (interactive). For simple commands we return
        // a placeholder indicating the exec was started.
        return new ExecResult
        {
            ExitCode = 0,
            StdOut = $"Exec session available at WebSocket: {execResult.Value.WebSocketUri}",
            StdErr = null
        };
    }

    private async Task<ContainerGroupResource> GetContainerGroupAsync(string name, CancellationToken ct)
    {
        var subscription = _armClient.GetSubscriptionResource(
            new AzureResourceIdentifier($"/subscriptions/{_subscriptionId}"));
        var rg = (await subscription.GetResourceGroupAsync(_resourceGroup, ct)).Value;
        return (await rg.GetContainerGroupAsync(name, ct)).Value;
    }

    private static ConnectionInfo GetConnectionInfoFromGroup(ContainerGroupResource group)
    {
        var ip = group.Data.IPAddress?.IP?.ToString();
        var ports = new Dictionary<int, int>();

        if (group.Data.IPAddress?.Ports is not null)
        {
            foreach (var port in group.Data.IPAddress.Ports)
            {
                ports[port.Port] = port.Port;
            }
        }

        return new ConnectionInfo
        {
            IpAddress = ip,
            PortMappings = ports,
            IdeEndpoint = ports.ContainsKey(8080) ? $"http://{ip}:8080" : null,
            VncEndpoint = ports.ContainsKey(6080) ? $"http://{ip}:6080" : null,
            SshEndpoint = ports.ContainsKey(22) ? $"ssh root@{ip}" : null
        };
    }
}
