using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Containers.Abstractions;
using Andy.Containers.Models;
using Microsoft.Extensions.Logging;
using ContainerSpec = Andy.Containers.Abstractions.ContainerSpec;
using ContainerStatus = Andy.Containers.Models.ContainerStatus;

namespace Andy.Containers.Infrastructure.Providers.ThirdParty;

public class FlyIoProvider : IInfrastructureProvider
{
    private readonly ILogger<FlyIoProvider> _logger;
    private readonly HttpClient _http;
    private readonly string _organization;
    private readonly string _region;
    private const string ApiBase = "https://api.machines.dev/v1";

    public ProviderType Type => ProviderType.FlyIo;

    public FlyIoProvider(string? connectionConfig, ILogger<FlyIoProvider> logger)
    {
        _logger = logger;
        _organization = "personal";
        _region = "iad";
        _http = new HttpClient();

        if (!string.IsNullOrEmpty(connectionConfig))
        {
            try
            {
                var config = JsonSerializer.Deserialize<JsonElement>(connectionConfig);
                if (config.TryGetProperty("apiToken", out var token))
                    _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetString());
                if (config.TryGetProperty("organization", out var org))
                    _organization = org.GetString() ?? _organization;
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
            Type = ProviderType.FlyIo,
            SupportedArchitectures = ["amd64"],
            SupportedOperatingSystems = ["linux"],
            MaxCpuCores = 16,
            MaxMemoryMb = 32768,
            MaxDiskGb = 50,
            SupportsGpu = true,
            GpuCapabilities =
            [
                new GpuCapability { Vendor = "NVIDIA", Model = "A100", MemoryMb = 81920, Count = 1, IsAvailable = true }
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
            var response = await _http.GetAsync($"{ApiBase}/apps?org_slug={_organization}", ct);
            return response.IsSuccessStatusCode ? ProviderHealth.Healthy : ProviderHealth.Degraded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fly.io health check failed");
            return ProviderHealth.Unreachable;
        }
    }

    public async Task<ContainerProvisionResult> CreateContainerAsync(ContainerSpec spec, CancellationToken ct)
    {
        _logger.LogInformation("Creating Fly.io machine {Name} from {Image}", spec.Name, spec.ImageReference);

        var appName = $"andy-{Guid.NewGuid().ToString("N")[..12]}";
        var resources = spec.Resources ?? new ResourceSpec();

        // Create the app first
        var appPayload = new { app_name = appName, org_slug = _organization };
        var appResponse = await _http.PostAsJsonAsync($"{ApiBase}/apps", appPayload, ct);
        appResponse.EnsureSuccessStatusCode();

        // Build machine config
        var (guestCpus, guestMemoryMb, cpuKind) = MapToFlyMachineSize(resources.CpuCores, resources.MemoryMb);

        var services = new List<object>();
        if (spec.PortMappings is not null)
        {
            foreach (var (containerPort, _) in spec.PortMappings)
            {
                services.Add(new
                {
                    ports = new[] { new { port = 443, handlers = new[] { "tls", "http" } } },
                    protocol = "tcp",
                    internal_port = containerPort
                });
            }
        }

        var env = spec.EnvironmentVariables ?? new Dictionary<string, string>();

        var machineConfig = new
        {
            image = spec.ImageReference,
            env,
            services,
            guest = new
            {
                cpu_kind = cpuKind,
                cpus = guestCpus,
                memory_mb = guestMemoryMb
            }
        };

        var machinePayload = new
        {
            region = _region,
            config = machineConfig,
            name = spec.Name.ToLowerInvariant().Replace(' ', '-')
        };

        var machineResponse = await _http.PostAsJsonAsync(
            $"{ApiBase}/apps/{appName}/machines", machinePayload, FlyJsonOptions, ct);
        machineResponse.EnsureSuccessStatusCode();

        var machine = await machineResponse.Content.ReadFromJsonAsync<FlyMachineResponse>(FlyJsonOptions, ct);
        var machineId = machine?.Id ?? throw new InvalidOperationException("Failed to create Fly machine");

        _logger.LogInformation("Fly.io machine {Id} created in app {App}", machineId, appName);

        // ExternalId format: "appName/machineId" to allow lookup
        return new ContainerProvisionResult
        {
            ExternalId = $"{appName}/{machineId}",
            Status = ContainerStatus.Running
        };
    }

    public async Task StartContainerAsync(string externalId, CancellationToken ct)
    {
        var (appName, machineId) = ParseExternalId(externalId);
        var response = await _http.PostAsync($"{ApiBase}/apps/{appName}/machines/{machineId}/start", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task StopContainerAsync(string externalId, CancellationToken ct)
    {
        var (appName, machineId) = ParseExternalId(externalId);
        var response = await _http.PostAsync($"{ApiBase}/apps/{appName}/machines/{machineId}/stop", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DestroyContainerAsync(string externalId, CancellationToken ct)
    {
        var (appName, machineId) = ParseExternalId(externalId);

        // Stop first, then destroy
        try { await StopContainerAsync(externalId, ct); } catch { }

        var response = await _http.DeleteAsync($"{ApiBase}/apps/{appName}/machines/{machineId}?force=true", ct);
        response.EnsureSuccessStatusCode();

        // Delete the app too
        await _http.DeleteAsync($"{ApiBase}/apps/{appName}", ct);
        _logger.LogInformation("Fly.io machine {Id} and app {App} destroyed", machineId, appName);
    }

    public async Task<ContainerRuntimeInfo> GetContainerInfoAsync(string externalId, CancellationToken ct)
    {
        var (appName, machineId) = ParseExternalId(externalId);
        var response = await _http.GetAsync($"{ApiBase}/apps/{appName}/machines/{machineId}", ct);
        response.EnsureSuccessStatusCode();

        var machine = await response.Content.ReadFromJsonAsync<FlyMachineResponse>(FlyJsonOptions, ct);

        var status = machine?.State switch
        {
            "started" => ContainerStatus.Running,
            "stopped" or "destroyed" => ContainerStatus.Stopped,
            _ => ContainerStatus.Pending
        };

        return new ContainerRuntimeInfo
        {
            ExternalId = externalId,
            Status = status,
            IpAddress = machine?.PrivateIp
        };
    }

    public Task<ContainerProvisionResult> ResizeContainerAsync(string externalId, ResourceSpec resources, CancellationToken ct)
    {
        throw new NotSupportedException("Fly.io does not support in-place resize. Stop and recreate the machine with updated resources.");
    }

    public async Task<ConnectionInfo> GetConnectionInfoAsync(string externalId, CancellationToken ct)
    {
        var info = await GetContainerInfoAsync(externalId, ct);
        var (appName, _) = ParseExternalId(externalId);

        return new ConnectionInfo
        {
            IpAddress = info.IpAddress,
            IdeEndpoint = $"https://{appName}.fly.dev"
        };
    }

    public Task<ExecResult> ExecAsync(string externalId, string command, CancellationToken ct)
    {
        return ExecAsync(externalId, command, TimeSpan.FromSeconds(30), ct);
    }

    public async Task<ExecResult> ExecAsync(string externalId, string command, TimeSpan timeout, CancellationToken ct)
    {
        var (appName, machineId) = ParseExternalId(externalId);

        // Fly Machines API supports exec via the /exec endpoint
        var payload = new { cmd = command, timeout = (int)timeout.TotalSeconds };
        var response = await _http.PostAsJsonAsync(
            $"{ApiBase}/apps/{appName}/machines/{machineId}/exec", payload, FlyJsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<FlyExecResponse>(FlyJsonOptions, ct);

        return new ExecResult
        {
            ExitCode = result?.ExitCode ?? -1,
            StdOut = result?.Stdout,
            StdErr = result?.Stderr
        };
    }

    private static (string appName, string machineId) ParseExternalId(string externalId)
    {
        var parts = externalId.Split('/');
        if (parts.Length != 2)
            throw new ArgumentException($"Invalid Fly.io external ID format: {externalId}. Expected 'appName/machineId'.");
        return (parts[0], parts[1]);
    }

    private static (int cpus, int memoryMb, string cpuKind) MapToFlyMachineSize(double cpuCores, int memoryMb)
    {
        // Fly machine sizes: shared-cpu-1x, performance-1x, performance-2x, etc.
        if (cpuCores <= 1 && memoryMb <= 512)
            return (1, 256, "shared");
        if (cpuCores <= 1)
            return (1, Math.Max(2048, memoryMb), "performance");
        if (cpuCores <= 2)
            return (2, Math.Max(4096, memoryMb), "performance");
        if (cpuCores <= 4)
            return (4, Math.Max(8192, memoryMb), "performance");
        if (cpuCores <= 8)
            return (8, Math.Max(16384, memoryMb), "performance");

        return (16, Math.Max(32768, memoryMb), "performance");
    }

    private static readonly JsonSerializerOptions FlyJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private class FlyMachineResponse
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? State { get; set; }
        public string? Region { get; set; }
        [JsonPropertyName("private_ip")]
        public string? PrivateIp { get; set; }
    }

    private class FlyExecResponse
    {
        [JsonPropertyName("exit_code")]
        public int ExitCode { get; set; }
        public string? Stdout { get; set; }
        public string? Stderr { get; set; }
    }
}
