using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.Containers.Client;

public sealed class ContainersClient
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;

    public ContainersClient(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };
    }

    // Containers

    public async Task<PaginatedResult<ContainerDto>> ListContainersAsync(int take = 20, CancellationToken ct = default)
    {
        var r = await _http.GetAsync($"api/containers?take={take}", ct);
        await EnsureSuccessAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<PaginatedResult<ContainerDto>>(_json, ct))!;
    }

    public async Task<ContainerDto> GetContainerAsync(string id, CancellationToken ct = default)
    {
        var r = await _http.GetAsync($"api/containers/{id}", ct);
        await EnsureSuccessAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<ContainerDto>(_json, ct))!;
    }

    public async Task<ContainerDto> CreateContainerAsync(string name, string templateCode, string? providerCode = null, CancellationToken ct = default)
    {
        var r = await _http.PostAsJsonAsync("api/containers", new { name, templateCode, providerCode, source = "Cli" }, _json, ct);
        await EnsureSuccessAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<ContainerDto>(_json, ct))!;
    }

    public async Task StartContainerAsync(string id, CancellationToken ct = default)
    {
        var r = await _http.PostAsync($"api/containers/{id}/start", null, ct);
        await EnsureSuccessAsync(r, ct);
    }

    public async Task StopContainerAsync(string id, CancellationToken ct = default)
    {
        var r = await _http.PostAsync($"api/containers/{id}/stop", null, ct);
        await EnsureSuccessAsync(r, ct);
    }

    public async Task DestroyContainerAsync(string id, CancellationToken ct = default)
    {
        var r = await _http.DeleteAsync($"api/containers/{id}", ct);
        await EnsureSuccessAsync(r, ct);
    }

    public async Task<ExecResultDto> ExecAsync(string id, string command, CancellationToken ct = default)
    {
        var r = await _http.PostAsJsonAsync($"api/containers/{id}/exec", new { command }, _json, ct);
        await EnsureSuccessAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<ExecResultDto>(_json, ct))!;
    }

    public async Task<ContainerStatsDto> GetStatsAsync(string id, CancellationToken ct = default)
    {
        var r = await _http.GetAsync($"api/containers/{id}/stats", ct);
        await EnsureSuccessAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<ContainerStatsDto>(_json, ct))!;
    }

    public async Task<ConnectionInfoDto> GetConnectionInfoAsync(string id, CancellationToken ct = default)
    {
        var r = await _http.GetAsync($"api/containers/{id}/connection", ct);
        await EnsureSuccessAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<ConnectionInfoDto>(_json, ct))!;
    }

    public async Task<ProviderDto[]> GetProvidersAsync(CancellationToken ct = default)
    {
        var r = await _http.GetAsync("api/providers", ct);
        await EnsureSuccessAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<ProviderDto[]>(_json, ct))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? body = null;
        try { body = await response.Content.ReadAsStringAsync(ct); } catch { }
        throw new ContainersApiException(response.StatusCode, body);
    }

    // DTOs

    public record ContainerDto(
        string Id, string Name, string Status, string? ExternalId, string OwnerId,
        string? TemplateId, string? ProviderId, string? StartedAt, string? CreatedAt,
        string? HostIp, string? IdeEndpoint, string? VncEndpoint,
        TemplateDto? Template, ProviderDto? Provider);

    public record TemplateDto(string Id, string Code, string Name, string? BaseImage);
    public record ProviderDto(string Id, string Code, string Name, string Type);
    public record PaginatedResult<T>(T[] Items, int TotalCount);
    public record ExecResultDto(int ExitCode, string? StdOut, string? StdErr);
    public record ContainerStatsDto(double CpuPercent, long MemoryUsageBytes, long MemoryLimitBytes,
        double MemoryPercent, long DiskUsageBytes, long DiskLimitBytes, double DiskPercent);
    public record ConnectionInfoDto(string? IpAddress, string? SshEndpoint, string? IdeEndpoint,
        string? VncEndpoint, Dictionary<string, int>? PortMappings);
}

public sealed class ContainersApiException : HttpRequestException
{
    public new HttpStatusCode StatusCode { get; }
    public string? ResponseBody { get; }

    public ContainersApiException(HttpStatusCode statusCode, string? responseBody)
        : base($"API returned {(int)statusCode} {statusCode}" +
               (string.IsNullOrWhiteSpace(responseBody) ? "" : $": {responseBody}"))
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
