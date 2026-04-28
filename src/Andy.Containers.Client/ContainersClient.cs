using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Containers.Messaging;
using Andy.Containers.Models;

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

    public async Task<ContainerDto> CreateContainerAsync(string name, string templateCode,
        string? providerCode = null, string? codeAssistant = null,
        string? model = null, string? baseUrl = null, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["templateCode"] = templateCode,
            ["source"] = "Cli"
        };
        if (providerCode is not null) payload["providerCode"] = providerCode;
        if (codeAssistant is not null)
        {
            var assistant = new Dictionary<string, object?> { ["tool"] = codeAssistant };
            if (model is not null) assistant["modelName"] = model;
            if (baseUrl is not null) assistant["apiBaseUrl"] = baseUrl;
            payload["codeAssistant"] = assistant;
        }
        var r = await _http.PostAsJsonAsync("api/containers", payload, _json, ct);
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

    // Runs (AP9 — rivoli-ai/andy-containers#111).
    //
    // Wire shapes are the server-side DTOs from Andy.Containers.Models:
    // RunDto and RunEventDto. Deserializing those keeps the client and
    // server schemas in lockstep at compile time — schema drift becomes
    // a build break instead of a runtime KeyNotFoundException. The
    // client lib already references Andy.Containers for Permissions
    // constants, so this is no extra dependency.

    // Environments (X7 — rivoli-ai/andy-containers#97). Read-only catalog.
    // The X3 server returns the standard { items, totalCount } envelope;
    // PaginatedResult<EnvironmentProfileDto> deserialises it directly via
    // the case-insensitive options the client already configures.

    public async Task<PaginatedResult<EnvironmentProfileDto>> ListEnvironmentsAsync(
        string? kind = null, int? skip = null, int? take = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(kind)) query.Add($"kind={Uri.EscapeDataString(kind)}");
        if (skip.HasValue) query.Add($"skip={skip.Value}");
        if (take.HasValue) query.Add($"take={take.Value}");
        var url = "api/environments" + (query.Count > 0 ? "?" + string.Join("&", query) : "");

        var r = await _http.GetAsync(url, ct);
        await EnsureSuccessAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<PaginatedResult<EnvironmentProfileDto>>(_json, ct))!;
    }

    public async Task<EnvironmentProfileDto> GetEnvironmentByCodeAsync(
        string code, CancellationToken ct = default)
    {
        var r = await _http.GetAsync($"api/environments/by-code/{Uri.EscapeDataString(code)}", ct);
        await EnsureSuccessAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<EnvironmentProfileDto>(_json, ct))!;
    }

    public async Task<RunDto> CreateRunAsync(CreateRunRequest request, CancellationToken ct = default)
    {
        var r = await _http.PostAsJsonAsync("api/runs", request, _json, ct);
        await EnsureSuccessAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<RunDto>(_json, ct))!;
    }

    public async Task<RunDto> GetRunAsync(string id, CancellationToken ct = default)
    {
        var r = await _http.GetAsync($"api/runs/{id}", ct);
        await EnsureSuccessAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<RunDto>(_json, ct))!;
    }

    public async Task<RunDto> CancelRunAsync(string id, CancellationToken ct = default)
    {
        var r = await _http.PostAsync($"api/runs/{id}/cancel", null, ct);
        await EnsureSuccessAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<RunDto>(_json, ct))!;
    }

    /// <summary>
    /// Stream lifecycle events for a run from
    /// <c>GET /api/runs/{id}/events</c>. The server writes NDJSON
    /// (one <see cref="RunEventDto"/> per line) and closes the
    /// response when the run reaches a terminal status. Caller cancels
    /// by disposing the enumerator or signalling <paramref name="ct"/>.
    /// </summary>
    public async IAsyncEnumerable<RunEventDto> StreamRunEventsAsync(
        string id,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // HttpCompletionOption.ResponseHeadersRead unblocks Send before
        // the body arrives so we can iterate as bytes land — without it
        // HttpClient buffers the whole response and the streaming UX
        // collapses to one batch at terminal.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/runs/{id}/events");
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);

        // EventJson is the canonical wire-shape options (snake_case +
        // ADR-0001-aligned converters). Using the server-side shared
        // options keeps the client deserialization aligned with what
        // the server actually wrote.
        var jsonOptions = EventJson.Options;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) yield break;
            if (line.Length == 0) continue;

            RunEventDto? evt;
            try
            {
                evt = JsonSerializer.Deserialize<RunEventDto>(line, jsonOptions);
            }
            catch (JsonException)
            {
                // Skip malformed lines rather than killing the stream;
                // the server should never produce them, but a partial
                // line at disconnect would otherwise blow up the CLI.
                continue;
            }

            if (evt is not null) yield return evt;
        }
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
        TemplateDto? Template, ProviderDto? Provider,
        // Conductor #871: identity fields the UI uses to label
        // containers — FriendlyName is generated at create time,
        // OsLabel is probed post-provisioning. Both nullable since
        // older containers (pre-migration) won't have either.
        string? FriendlyName, string? OsLabel);

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
