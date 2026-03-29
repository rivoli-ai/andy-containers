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

public class CivoProvider : IInfrastructureProvider
{
    private readonly ILogger<CivoProvider> _logger;
    private readonly HttpClient _http;
    private readonly string _region;
    private const string ApiBase = "https://api.civo.com/v2";

    public ProviderType Type => ProviderType.Civo;

    public CivoProvider(string? connectionConfig, ILogger<CivoProvider> logger)
    {
        _logger = logger;
        _region = "LON1";
        _http = new HttpClient();

        if (!string.IsNullOrEmpty(connectionConfig))
        {
            try
            {
                var config = JsonSerializer.Deserialize<JsonElement>(connectionConfig);
                if (config.TryGetProperty("apiKey", out var key))
                    _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.GetString());
                if (config.TryGetProperty("region", out var region))
                    _region = region.GetString() ?? _region;
            }
            catch { }
        }
    }

    public Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        return Task.FromResult(new ProviderCapabilities
        {
            Type = ProviderType.Civo,
            SupportedArchitectures = ["amd64"],
            SupportedOperatingSystems = ["linux"],
            MaxCpuCores = 200,
            MaxMemoryMb = 393216,
            MaxDiskGb = 800,
            SupportsGpu = true,
            GpuCapabilities =
            [
                new GpuCapability { Vendor = "NVIDIA", Model = "A100", MemoryMb = 81920, Count = 1, IsAvailable = true },
                new GpuCapability { Vendor = "NVIDIA", Model = "L40S", MemoryMb = 49152, Count = 1, IsAvailable = true }
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
            var response = await _http.GetAsync($"{ApiBase}/regions", ct);
            return response.IsSuccessStatusCode ? ProviderHealth.Healthy : ProviderHealth.Degraded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Civo health check failed");
            return ProviderHealth.Unreachable;
        }
    }

    public async Task<ContainerProvisionResult> CreateContainerAsync(ContainerSpec spec, CancellationToken ct)
    {
        _logger.LogInformation("Creating Civo instance for container {Name} from {Image}", spec.Name, spec.ImageReference);

        var resources = spec.Resources ?? new ResourceSpec();
        var size = MapToInstanceSize(resources.CpuCores, resources.MemoryMb);
        var containerName = $"andy-{Guid.NewGuid().ToString("N")[..12]}";

        var cloudInit = SshDockerHelper.GetCloudInitScript(spec.ImageReference, containerName, spec.PortMappings);
        var cloudInitBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(cloudInit));

        var payload = new
        {
            hostname = containerName,
            size,
            region = _region,
            template_id = "ubuntu-jammy", // Civo template for Ubuntu
            script = cloudInitBase64,
            tags = "andy-containers"
        };

        var response = await _http.PostAsJsonAsync($"{ApiBase}/instances", payload, CivoJsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CivoInstanceResponse>(CivoJsonOptions, ct);
        var instanceId = result?.Id
            ?? throw new InvalidOperationException("Failed to create Civo instance");

        _logger.LogInformation("Civo instance {Id} created for container {Name}", instanceId, containerName);

        return new ContainerProvisionResult
        {
            ExternalId = $"{instanceId}/{containerName}",
            Status = ContainerStatus.Pending
        };
    }

    public async Task StartContainerAsync(string externalId, CancellationToken ct)
    {
        var (instanceId, _) = ParseExternalId(externalId);
        var response = await _http.PutAsync($"{ApiBase}/instances/{instanceId}/start", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task StopContainerAsync(string externalId, CancellationToken ct)
    {
        var (instanceId, _) = ParseExternalId(externalId);
        var response = await _http.PutAsync($"{ApiBase}/instances/{instanceId}/stop", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DestroyContainerAsync(string externalId, CancellationToken ct)
    {
        var (instanceId, _) = ParseExternalId(externalId);
        var response = await _http.DeleteAsync($"{ApiBase}/instances/{instanceId}", ct);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Civo instance {Id} destroyed", instanceId);
    }

    public async Task<ContainerRuntimeInfo> GetContainerInfoAsync(string externalId, CancellationToken ct)
    {
        var (instanceId, _) = ParseExternalId(externalId);
        var response = await _http.GetAsync($"{ApiBase}/instances/{instanceId}", ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CivoInstanceResponse>(CivoJsonOptions, ct);

        var status = result?.Status switch
        {
            "ACTIVE" => ContainerStatus.Running,
            "SHUTOFF" => ContainerStatus.Stopped,
            "BUILD" => ContainerStatus.Pending,
            _ => ContainerStatus.Pending
        };

        return new ContainerRuntimeInfo
        {
            ExternalId = externalId,
            Status = status,
            IpAddress = result?.PublicIp
        };
    }

    public Task<ContainerProvisionResult> ResizeContainerAsync(string externalId, ResourceSpec resources, CancellationToken ct)
    {
        throw new NotSupportedException("Civo instance resize is not supported in-place. Destroy and recreate with new resources.");
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
            throw new InvalidOperationException("Instance IP address not available");

        using var sshHelper = new SshDockerHelper(_logger);
        sshHelper.ConnectWithPassword(connInfo.IpAddress, "root", ""); // Expects SSH key auth
        return sshHelper.DockerExec(containerName, command);
    }

    private static (string instanceId, string containerName) ParseExternalId(string externalId)
    {
        var parts = externalId.Split('/');
        if (parts.Length != 2)
            throw new ArgumentException($"Invalid Civo external ID format: {externalId}. Expected 'instanceId/containerName'.");
        return (parts[0], parts[1]);
    }

    private static string MapToInstanceSize(double cpuCores, int memoryMb)
    {
        return cpuCores switch
        {
            <= 1 when memoryMb <= 1024 => "g3.xsmall",
            <= 1 when memoryMb <= 2048 => "g3.small",
            <= 2 when memoryMb <= 4096 => "g3.medium",
            <= 4 when memoryMb <= 8192 => "g3.large",
            <= 8 when memoryMb <= 16384 => "g3.xlarge",
            _ => "g3.2xlarge"
        };
    }

    private static readonly JsonSerializerOptions CivoJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private class CivoInstanceResponse
    {
        public string? Id { get; set; }
        public string? Hostname { get; set; }
        public string? Status { get; set; }
        [JsonPropertyName("public_ip")]
        public string? PublicIp { get; set; }
    }
}
