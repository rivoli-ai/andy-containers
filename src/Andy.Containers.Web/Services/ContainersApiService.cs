using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.Containers.Web.Services;

public class ContainersApiService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ContainersApiService(HttpClient http)
    {
        _http = http;
    }

    // ---------- Containers ----------

    public async Task<PagedResult<ContainerDto>> GetContainersAsync(int skip = 0, int take = 20)
    {
        var response = await _http.GetFromJsonAsync<PagedResult<ContainerDto>>(
            $"api/containers?skip={skip}&take={take}", JsonOptions);
        return response ?? new();
    }

    public async Task<ContainerDto?> GetContainerAsync(Guid id)
    {
        return await _http.GetFromJsonAsync<ContainerDto>($"api/containers/{id}", JsonOptions);
    }

    public async Task StartContainerAsync(Guid id)
    {
        await _http.PostAsync($"api/containers/{id}/start", null);
    }

    public async Task StopContainerAsync(Guid id)
    {
        await _http.PostAsync($"api/containers/{id}/stop", null);
    }

    public async Task DestroyContainerAsync(Guid id)
    {
        await _http.DeleteAsync($"api/containers/{id}");
    }

    public async Task<ConnectionInfoDto?> GetConnectionInfoAsync(Guid id)
    {
        return await _http.GetFromJsonAsync<ConnectionInfoDto>(
            $"api/containers/{id}/connection", JsonOptions);
    }

    public async Task<ExecResultDto?> ExecAsync(Guid id, ExecRequestDto request)
    {
        var response = await _http.PostAsJsonAsync($"api/containers/{id}/exec", request, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ExecResultDto>(JsonOptions);
    }

    public async Task<List<ContainerEventDto>> GetContainerEventsAsync(Guid id)
    {
        var response = await _http.GetFromJsonAsync<List<ContainerEventDto>>(
            $"api/containers/{id}/events", JsonOptions);
        return response ?? [];
    }

    public async Task<ContainerDto?> CreateContainerAsync(CreateContainerRequest request)
    {
        var response = await _http.PostAsJsonAsync("api/containers", request, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
    }

    // ---------- Templates ----------

    public async Task<PagedResult<TemplateDto>> GetTemplatesAsync(string? search = null, int skip = 0, int take = 20)
    {
        var url = $"api/templates?skip={skip}&take={take}";
        if (!string.IsNullOrWhiteSpace(search))
            url += $"&search={Uri.EscapeDataString(search)}";
        var response = await _http.GetFromJsonAsync<PagedResult<TemplateDto>>(url, JsonOptions);
        return response ?? new();
    }

    public async Task<TemplateDto?> GetTemplateAsync(Guid id)
    {
        return await _http.GetFromJsonAsync<TemplateDto>($"api/templates/{id}", JsonOptions);
    }

    public async Task<TemplateDefinitionDto?> GetTemplateDefinitionAsync(Guid id)
    {
        try
        {
            return await _http.GetFromJsonAsync<TemplateDefinitionDto>(
                $"api/templates/{id}/definition", JsonOptions);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<YamlValidationResultDto?> ValidateTemplateYamlAsync(string yaml)
    {
        var response = await _http.PostAsJsonAsync("api/templates/validate", new { content = yaml }, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<YamlValidationResultDto>(JsonOptions);
    }

    public async Task<TemplateDto?> CreateTemplateFromYamlAsync(string yaml)
    {
        var response = await _http.PostAsJsonAsync("api/templates/from-yaml", new { content = yaml }, JsonOptions);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<YamlValidationResultDto>(JsonOptions);
            throw new YamlValidationException(error);
        }
        return await response.Content.ReadFromJsonAsync<TemplateDto>(JsonOptions);
    }

    public async Task<TemplateDto?> UpdateTemplateDefinitionAsync(Guid id, string yaml)
    {
        var response = await _http.PutAsJsonAsync($"api/templates/{id}/definition", new { content = yaml }, JsonOptions);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var validation = await response.Content.ReadFromJsonAsync<YamlValidationResultDto>(JsonOptions);
            throw new YamlValidationException(validation);
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TemplateDto>(JsonOptions);
    }

    // ---------- Providers ----------

    public async Task<List<ProviderDto>> GetProvidersAsync()
    {
        var response = await _http.GetFromJsonAsync<List<ProviderDto>>("api/providers", JsonOptions);
        return response ?? [];
    }

    public async Task<ProviderDto?> GetProviderAsync(Guid id)
    {
        return await _http.GetFromJsonAsync<ProviderDto>($"api/providers/{id}", JsonOptions);
    }

    public async Task<ProviderHealthResult?> CheckProviderHealthAsync(Guid id)
    {
        var response = await _http.GetFromJsonAsync<ProviderHealthResult>(
            $"api/providers/{id}/health", JsonOptions);
        return response;
    }

    public async Task<CostEstimateDto?> GetCostEstimateAsync(Guid providerId, double cpuCores = 2, int memoryMb = 4096)
    {
        return await _http.GetFromJsonAsync<CostEstimateDto>(
            $"api/providers/{providerId}/cost-estimate?cpuCores={cpuCores}&memoryMb={memoryMb}", JsonOptions);
    }

    // ---------- Workspaces ----------

    public async Task<PagedResult<WorkspaceDto>> GetWorkspacesAsync(int skip = 0, int take = 20)
    {
        var response = await _http.GetFromJsonAsync<PagedResult<WorkspaceDto>>(
            $"api/workspaces?skip={skip}&take={take}", JsonOptions);
        return response ?? new();
    }

    public async Task<WorkspaceDto?> GetWorkspaceAsync(Guid id)
    {
        return await _http.GetFromJsonAsync<WorkspaceDto>($"api/workspaces/{id}", JsonOptions);
    }
}

// ---------- DTOs ----------

public class PagedResult<T>
{
    public List<T> Items { get; set; } = [];
    public int TotalCount { get; set; }
}

public class ContainerDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid TemplateId { get; set; }
    public Guid ProviderId { get; set; }
    public string? ExternalId { get; set; }
    public string Status { get; set; } = "Pending";
    public string OwnerId { get; set; } = "";
    public string? IdeEndpoint { get; set; }
    public string? VncEndpoint { get; set; }
    public string? GitRepository { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? StoppedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastActivityAt { get; set; }
}

public class TemplateDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Version { get; set; } = "";
    public string BaseImage { get; set; } = "";
    public string CatalogScope { get; set; } = "Global";
    public string IdeType { get; set; } = "CodeServer";
    public bool GpuRequired { get; set; }
    public bool GpuPreferred { get; set; }
    public string[]? Tags { get; set; }
    public bool IsPublished { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class ProviderDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Docker";
    public string? Region { get; set; }
    public bool IsEnabled { get; set; }
    public string HealthStatus { get; set; } = "Unknown";
    public DateTime? LastHealthCheck { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ProviderHealthResult
{
    public string Status { get; set; } = "Unknown";
    public string? Error { get; set; }
}

public class WorkspaceDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string OwnerId { get; set; } = "";
    public string Status { get; set; } = "Active";
    public string? GitRepositoryUrl { get; set; }
    public string? GitBranch { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? LastAccessedAt { get; set; }
    public ContainerDto? DefaultContainer { get; set; }
}

public class CreateContainerRequest
{
    public required string Name { get; set; }
    public Guid? TemplateId { get; set; }
    public string? TemplateCode { get; set; }
    public Guid? ProviderId { get; set; }
    public string? ProviderCode { get; set; }
    public GitRepositoryConfig? GitRepository { get; set; }
}

public class GitRepositoryConfig
{
    public required string Url { get; set; }
    public string? Branch { get; set; }
}

public class ConnectionInfoDto
{
    public string? IpAddress { get; set; }
    public Dictionary<int, int>? PortMappings { get; set; }
    public string? IdeEndpoint { get; set; }
    public string? VncEndpoint { get; set; }
    public string? SshEndpoint { get; set; }
    public string? AgentEndpoint { get; set; }
}

public class ExecResultDto
{
    public int ExitCode { get; set; }
    public string? StdOut { get; set; }
    public string? StdErr { get; set; }
}

public class ExecRequestDto
{
    public required string Command { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}

public class ContainerEventDto
{
    public Guid Id { get; set; }
    public Guid ContainerId { get; set; }
    public string EventType { get; set; } = "";
    public string? SubjectId { get; set; }
    public string? Details { get; set; }
    public DateTime Timestamp { get; set; }
}

public class TemplateDefinitionDto
{
    public string Code { get; set; } = "";
    public string Content { get; set; } = "";
}

public class CostEstimateDto
{
    public decimal HourlyCostUsd { get; set; }
    public decimal MonthlyCostUsd { get; set; }
    public string? FreeTierNote { get; set; }
    public CostBreakdownDto[]? Breakdown { get; set; }
}

public class CostBreakdownDto
{
    public string Component { get; set; } = "";
    public decimal HourlyCostUsd { get; set; }
    public string? Unit { get; set; }
}

public class YamlValidationResultDto
{
    public bool IsValid { get; set; }
    public List<YamlValidationErrorDto> Errors { get; set; } = [];
    public List<YamlValidationWarningDto> Warnings { get; set; } = [];
}

public class YamlValidationErrorDto
{
    public string Field { get; set; } = "";
    public string Message { get; set; } = "";
    public int? Line { get; set; }
}

public class YamlValidationWarningDto
{
    public string Field { get; set; } = "";
    public string Message { get; set; } = "";
    public int? Line { get; set; }
}

public class YamlValidationException : Exception
{
    public YamlValidationResultDto? ValidationResult { get; }
    public YamlValidationException(YamlValidationResultDto? result) : base("YAML validation failed")
    {
        ValidationResult = result;
    }
}
