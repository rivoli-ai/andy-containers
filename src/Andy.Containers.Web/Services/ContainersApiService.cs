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
