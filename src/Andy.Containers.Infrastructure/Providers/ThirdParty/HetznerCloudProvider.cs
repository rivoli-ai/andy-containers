using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Providers.Shared;
using Andy.Containers.Models;
using Microsoft.Extensions.Logging;
using ContainerSpec = Andy.Containers.Abstractions.ContainerSpec;
using ContainerStatus = Andy.Containers.Models.ContainerStatus;

namespace Andy.Containers.Infrastructure.Providers.ThirdParty;

public class HetznerCloudProvider : IInfrastructureProvider
{
    private readonly ILogger<HetznerCloudProvider> _logger;
    private readonly HttpClient _http;
    private readonly string _location;
    private readonly string? _sshKeyId;
    private const string ApiBase = "https://api.hetzner.cloud/v1";

    public ProviderType Type => ProviderType.Hetzner;

    public HetznerCloudProvider(string? connectionConfig, ILogger<HetznerCloudProvider> logger)
    {
        _logger = logger;
        _location = "fsn1";
        _http = new HttpClient();

        if (!string.IsNullOrEmpty(connectionConfig))
        {
            try
            {
                var config = JsonSerializer.Deserialize<JsonElement>(connectionConfig);
                if (config.TryGetProperty("apiToken", out var token))
                    _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetString());
                if (config.TryGetProperty("location", out var loc))
                    _location = loc.GetString() ?? _location;
                if (config.TryGetProperty("sshKeyId", out var sshKey))
                    _sshKeyId = sshKey.GetString();
            }
            catch { }
        }
    }

    public Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        return Task.FromResult(new ProviderCapabilities
        {
            Type = ProviderType.Hetzner,
            SupportedArchitectures = ["amd64", "arm64"],
            SupportedOperatingSystems = ["linux"],
            MaxCpuCores = 48,
            MaxMemoryMb = 196608,
            MaxDiskGb = 960,
            SupportsGpu = false,
            SupportsVolumeMount = false,
            SupportsPortForwarding = true,
            SupportsExec = true, // via SSH -> docker exec
            SupportsStreaming = false,
            SupportsOfflineBuild = false
        });
    }

    public async Task<ProviderHealth> HealthCheckAsync(CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync($"{ApiBase}/servers?per_page=1", ct);
            return response.IsSuccessStatusCode ? ProviderHealth.Healthy : ProviderHealth.Degraded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hetzner health check failed");
            return ProviderHealth.Unreachable;
        }
    }

    public async Task<ContainerProvisionResult> CreateContainerAsync(ContainerSpec spec, CancellationToken ct)
    {
        _logger.LogInformation("Creating Hetzner server for container {Name} from {Image}", spec.Name, spec.ImageReference);

        var resources = spec.Resources ?? new ResourceSpec();
        var serverType = MapToServerType(resources.CpuCores, resources.MemoryMb);
        var containerName = $"andy-{Guid.NewGuid().ToString("N")[..12]}";
        var serverName = containerName;

        var cloudInit = SshDockerHelper.GetCloudInitScript(spec.ImageReference, containerName, spec.PortMappings);

        var payload = new
        {
            name = serverName,
            server_type = serverType,
            image = "ubuntu-24.04",
            location = _location,
            ssh_keys = _sshKeyId is not null ? new[] { _sshKeyId } : Array.Empty<string>(),
            user_data = cloudInit,
            labels = new Dictionary<string, string>
            {
                ["managed-by"] = "andy-containers",
                ["container-name"] = containerName,
                ["container-image"] = spec.ImageReference
            }
        };

        var response = await _http.PostAsJsonAsync($"{ApiBase}/servers", payload, HetznerJsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<HetznerCreateServerResponse>(HetznerJsonOptions, ct);
        var serverId = result?.Server?.Id.ToString()
            ?? throw new InvalidOperationException("Failed to create Hetzner server");

        _logger.LogInformation("Hetzner server {Id} created for container {Name}", serverId, containerName);

        // ExternalId format: "serverId/containerName"
        return new ContainerProvisionResult
        {
            ExternalId = $"{serverId}/{containerName}",
            Status = ContainerStatus.Pending, // Server takes time to boot + cloud-init
            ConnectionInfo = new ConnectionInfo
            {
                IpAddress = result.Server?.PublicNet?.Ipv4?.Ip
            }
        };
    }

    public async Task StartContainerAsync(string externalId, CancellationToken ct)
    {
        var (serverId, _) = ParseExternalId(externalId);
        var response = await _http.PostAsync($"{ApiBase}/servers/{serverId}/actions/poweron", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task StopContainerAsync(string externalId, CancellationToken ct)
    {
        var (serverId, _) = ParseExternalId(externalId);
        var response = await _http.PostAsync($"{ApiBase}/servers/{serverId}/actions/shutdown", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DestroyContainerAsync(string externalId, CancellationToken ct)
    {
        var (serverId, _) = ParseExternalId(externalId);
        var response = await _http.DeleteAsync($"{ApiBase}/servers/{serverId}", ct);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Hetzner server {Id} destroyed", serverId);
    }

    public async Task<ContainerRuntimeInfo> GetContainerInfoAsync(string externalId, CancellationToken ct)
    {
        var (serverId, containerName) = ParseExternalId(externalId);
        var response = await _http.GetAsync($"{ApiBase}/servers/{serverId}", ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<HetznerGetServerResponse>(HetznerJsonOptions, ct);
        var server = result?.Server;

        var status = server?.Status switch
        {
            "running" => ContainerStatus.Running,
            "off" or "stopping" => ContainerStatus.Stopped,
            "initializing" or "starting" => ContainerStatus.Pending,
            _ => ContainerStatus.Pending
        };

        return new ContainerRuntimeInfo
        {
            ExternalId = externalId,
            Status = status,
            IpAddress = server?.PublicNet?.Ipv4?.Ip
        };
    }

    public Task<ContainerProvisionResult> ResizeContainerAsync(string externalId, ResourceSpec resources, CancellationToken ct)
    {
        throw new NotSupportedException("Hetzner server resize requires a reboot. Destroy and recreate with new resources.");
    }

    public async Task<ConnectionInfo> GetConnectionInfoAsync(string externalId, CancellationToken ct)
    {
        var info = await GetContainerInfoAsync(externalId, ct);
        return new ConnectionInfo
        {
            IpAddress = info.IpAddress,
            SshEndpoint = info.IpAddress is not null ? $"ssh root@{info.IpAddress}" : null
        };
    }

    public Task<ContainerStats> GetContainerStatsAsync(string externalId, CancellationToken ct)
    {
        return Task.FromResult(new ContainerStats());
    }

    public Task<ExecResult> ExecAsync(string externalId, string command, CancellationToken ct)
    {
        return ExecAsync(externalId, command, TimeSpan.FromSeconds(30), ct);
    }

    public async Task<ExecResult> ExecAsync(string externalId, string command, TimeSpan timeout, CancellationToken ct)
    {
        var (_, containerName) = ParseExternalId(externalId);
        var connInfo = await GetConnectionInfoAsync(externalId, ct);

        if (string.IsNullOrEmpty(connInfo.IpAddress))
            throw new InvalidOperationException("Server IP address not available");

        using var sshHelper = new SshDockerHelper(_logger);
        sshHelper.ConnectWithPassword(connInfo.IpAddress, "root", ""); // Expects SSH key auth
        return sshHelper.DockerExec(containerName, command);
    }

    private static (string serverId, string containerName) ParseExternalId(string externalId)
    {
        var parts = externalId.Split('/');
        if (parts.Length != 2)
            throw new ArgumentException($"Invalid Hetzner external ID format: {externalId}. Expected 'serverId/containerName'.");
        return (parts[0], parts[1]);
    }

    private static string MapToServerType(double cpuCores, int memoryMb)
    {
        // Hetzner server types (shared): cx22, cx32, cx42, cx52
        // ARM types: cax11, cax21, cax31, cax41
        return cpuCores switch
        {
            <= 2 when memoryMb <= 4096 => "cx22",
            <= 4 when memoryMb <= 8192 => "cx32",
            <= 8 when memoryMb <= 16384 => "cx42",
            <= 16 when memoryMb <= 32768 => "cx52",
            _ => "cx52"
        };
    }

    private static readonly JsonSerializerOptions HetznerJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private class HetznerCreateServerResponse
    {
        public HetznerServer? Server { get; set; }
    }

    private class HetznerGetServerResponse
    {
        public HetznerServer? Server { get; set; }
    }

    private class HetznerServer
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string? Status { get; set; }
        [JsonPropertyName("public_net")]
        public HetznerPublicNet? PublicNet { get; set; }
    }

    private class HetznerPublicNet
    {
        public HetznerIp? Ipv4 { get; set; }
    }

    private class HetznerIp
    {
        public string? Ip { get; set; }
    }
}
