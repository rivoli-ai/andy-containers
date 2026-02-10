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

public class DigitalOceanProvider : IInfrastructureProvider
{
    private readonly ILogger<DigitalOceanProvider> _logger;
    private readonly HttpClient _http;
    private readonly string _region;
    private readonly string? _sshKeyFingerprint;
    private const string ApiBase = "https://api.digitalocean.com/v2";

    public ProviderType Type => ProviderType.DigitalOcean;

    public DigitalOceanProvider(string? connectionConfig, ILogger<DigitalOceanProvider> logger)
    {
        _logger = logger;
        _region = "nyc1";
        _http = new HttpClient();

        if (!string.IsNullOrEmpty(connectionConfig))
        {
            try
            {
                var config = JsonSerializer.Deserialize<JsonElement>(connectionConfig);
                if (config.TryGetProperty("apiToken", out var token))
                    _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetString());
                if (config.TryGetProperty("region", out var region))
                    _region = region.GetString() ?? _region;
                if (config.TryGetProperty("sshKeyFingerprint", out var sshKey))
                    _sshKeyFingerprint = sshKey.GetString();
            }
            catch { }
        }
    }

    public Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        return Task.FromResult(new ProviderCapabilities
        {
            Type = ProviderType.DigitalOcean,
            SupportedArchitectures = ["amd64"],
            SupportedOperatingSystems = ["linux"],
            MaxCpuCores = 48,
            MaxMemoryMb = 262144,
            MaxDiskGb = 2400,
            SupportsGpu = true,
            GpuCapabilities =
            [
                new GpuCapability { Vendor = "NVIDIA", Model = "H100", MemoryMb = 81920, Count = 1, IsAvailable = true }
            ],
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
            var response = await _http.GetAsync($"{ApiBase}/account", ct);
            return response.IsSuccessStatusCode ? ProviderHealth.Healthy : ProviderHealth.Degraded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DigitalOcean health check failed");
            return ProviderHealth.Unreachable;
        }
    }

    public async Task<ContainerProvisionResult> CreateContainerAsync(ContainerSpec spec, CancellationToken ct)
    {
        _logger.LogInformation("Creating DigitalOcean droplet for container {Name} from {Image}", spec.Name, spec.ImageReference);

        var resources = spec.Resources ?? new ResourceSpec();
        var size = MapToDropletSize(resources.CpuCores, resources.MemoryMb);
        var containerName = $"andy-{Guid.NewGuid().ToString("N")[..12]}";
        var dropletName = containerName;

        var cloudInit = SshDockerHelper.GetCloudInitScript(spec.ImageReference, containerName, spec.PortMappings);

        var payload = new
        {
            name = dropletName,
            region = _region,
            size,
            image = "ubuntu-24-04-x64",
            ssh_keys = _sshKeyFingerprint is not null ? new[] { _sshKeyFingerprint } : Array.Empty<string>(),
            user_data = cloudInit,
            tags = new[] { "andy-containers", containerName }
        };

        var response = await _http.PostAsJsonAsync($"{ApiBase}/droplets", payload, DoJsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<DoCreateDropletResponse>(DoJsonOptions, ct);
        var dropletId = result?.Droplet?.Id.ToString()
            ?? throw new InvalidOperationException("Failed to create DigitalOcean droplet");

        _logger.LogInformation("DigitalOcean droplet {Id} created for container {Name}", dropletId, containerName);

        return new ContainerProvisionResult
        {
            ExternalId = $"{dropletId}/{containerName}",
            Status = ContainerStatus.Pending
        };
    }

    public async Task StartContainerAsync(string externalId, CancellationToken ct)
    {
        var (dropletId, _) = ParseExternalId(externalId);
        var payload = new { type = "power_on" };
        var response = await _http.PostAsJsonAsync($"{ApiBase}/droplets/{dropletId}/actions", payload, DoJsonOptions, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task StopContainerAsync(string externalId, CancellationToken ct)
    {
        var (dropletId, _) = ParseExternalId(externalId);
        var payload = new { type = "shutdown" };
        var response = await _http.PostAsJsonAsync($"{ApiBase}/droplets/{dropletId}/actions", payload, DoJsonOptions, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DestroyContainerAsync(string externalId, CancellationToken ct)
    {
        var (dropletId, _) = ParseExternalId(externalId);
        var response = await _http.DeleteAsync($"{ApiBase}/droplets/{dropletId}", ct);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("DigitalOcean droplet {Id} destroyed", dropletId);
    }

    public async Task<ContainerRuntimeInfo> GetContainerInfoAsync(string externalId, CancellationToken ct)
    {
        var (dropletId, _) = ParseExternalId(externalId);
        var response = await _http.GetAsync($"{ApiBase}/droplets/{dropletId}", ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<DoGetDropletResponse>(DoJsonOptions, ct);
        var droplet = result?.Droplet;

        var status = droplet?.Status switch
        {
            "active" => ContainerStatus.Running,
            "off" => ContainerStatus.Stopped,
            "new" => ContainerStatus.Pending,
            _ => ContainerStatus.Pending
        };

        var ip = droplet?.Networks?.V4?.FirstOrDefault(n => n.Type == "public")?.IpAddress;

        return new ContainerRuntimeInfo
        {
            ExternalId = externalId,
            Status = status,
            IpAddress = ip
        };
    }

    public Task<ContainerProvisionResult> ResizeContainerAsync(string externalId, ResourceSpec resources, CancellationToken ct)
    {
        throw new NotSupportedException("DigitalOcean droplet resize requires power off. Destroy and recreate with new resources.");
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

    public Task<ExecResult> ExecAsync(string externalId, string command, CancellationToken ct)
    {
        return ExecAsync(externalId, command, TimeSpan.FromSeconds(30), ct);
    }

    public async Task<ExecResult> ExecAsync(string externalId, string command, TimeSpan timeout, CancellationToken ct)
    {
        var (_, containerName) = ParseExternalId(externalId);
        var connInfo = await GetConnectionInfoAsync(externalId, ct);

        if (string.IsNullOrEmpty(connInfo.IpAddress))
            throw new InvalidOperationException("Droplet IP address not available");

        using var sshHelper = new SshDockerHelper(_logger);
        sshHelper.ConnectWithPassword(connInfo.IpAddress, "root", ""); // Expects SSH key auth
        return sshHelper.DockerExec(containerName, command);
    }

    private static (string dropletId, string containerName) ParseExternalId(string externalId)
    {
        var parts = externalId.Split('/');
        if (parts.Length != 2)
            throw new ArgumentException($"Invalid DigitalOcean external ID format: {externalId}. Expected 'dropletId/containerName'.");
        return (parts[0], parts[1]);
    }

    private static string MapToDropletSize(double cpuCores, int memoryMb)
    {
        return cpuCores switch
        {
            <= 1 when memoryMb <= 1024 => "s-1vcpu-1gb",
            <= 1 when memoryMb <= 2048 => "s-1vcpu-2gb",
            <= 2 when memoryMb <= 2048 => "s-2vcpu-2gb",
            <= 2 when memoryMb <= 4096 => "s-2vcpu-4gb",
            <= 4 when memoryMb <= 8192 => "s-4vcpu-8gb",
            <= 8 when memoryMb <= 16384 => "s-8vcpu-16gb",
            _ => "s-8vcpu-16gb"
        };
    }

    private static readonly JsonSerializerOptions DoJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private class DoCreateDropletResponse
    {
        public DoDroplet? Droplet { get; set; }
    }

    private class DoGetDropletResponse
    {
        public DoDroplet? Droplet { get; set; }
    }

    private class DoDroplet
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string? Status { get; set; }
        public DoNetworks? Networks { get; set; }
    }

    private class DoNetworks
    {
        public DoNetworkV4[]? V4 { get; set; }
    }

    private class DoNetworkV4
    {
        [JsonPropertyName("ip_address")]
        public string? IpAddress { get; set; }
        public string? Type { get; set; }
    }
}
