using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using Andy.Rbac.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andy.Containers.Api.Controllers;

[ApiController]
[Route("api/api-keys")]
[Authorize]
public class ApiKeysController : ControllerBase
{
    private readonly IApiKeyService _apiKeyService;
    private readonly ICurrentUserService _currentUser;
    private readonly IOrganizationMembershipService _orgMembership;

    public ApiKeysController(
        IApiKeyService apiKeyService,
        ICurrentUserService currentUser,
        IOrganizationMembershipService orgMembership)
    {
        _apiKeyService = apiKeyService;
        _currentUser = currentUser;
        _orgMembership = orgMembership;
    }

    [RequirePermission("settings:write")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApiKeyDto dto, CancellationToken ct)
    {
        if (!Enum.TryParse<ApiKeyProvider>(dto.Provider, true, out var provider))
            return BadRequest(new { error = $"Invalid provider. Valid values: {string.Join(", ", Enum.GetNames<ApiKeyProvider>())}" });

        var userId = _currentUser.GetUserId();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();

        try
        {
            var credential = await _apiKeyService.CreateAsync(
                userId, dto.Label, provider, dto.ApiKey,
                dto.EnvVarName, dto.OrganizationId, ip, dto.BaseUrl, dto.ModelName, ct);

            return CreatedAtAction(nameof(Get), new { id = credential.Id }, ToDto(credential));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [RequirePermission("settings:read")]
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var userId = _currentUser.GetUserId();
        var keys = await _apiKeyService.ListAsync(userId, ct);
        return Ok(keys.Select(ToDto));
    }

    [RequirePermission("settings:read")]
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var userId = _currentUser.GetUserId();
        var key = await _apiKeyService.GetAsync(id, userId, ct);
        if (key is null) return NotFound();
        return Ok(ToDto(key));
    }

    [RequirePermission("settings:write")]
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateApiKeyDto dto, CancellationToken ct)
    {
        var userId = _currentUser.GetUserId();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var key = await _apiKeyService.UpdateAsync(id, userId, dto.Label, dto.ApiKey, ip, ct);
        if (key is null) return NotFound();
        return Ok(ToDto(key));
    }

    [RequirePermission("settings:write")]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var userId = _currentUser.GetUserId();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var deleted = await _apiKeyService.DeleteAsync(id, userId, ip, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }

    [RequirePermission("settings:write")]
    [HttpPost("{id:guid}/validate")]
    public async Task<IActionResult> Validate(Guid id, CancellationToken ct)
    {
        var userId = _currentUser.GetUserId();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var result = await _apiKeyService.ValidateExistingAsync(id, userId, ip, ct);
        return Ok(new { isValid = result.IsValid, error = result.Error });
    }

    [RequirePermission("settings:read")]
    [HttpGet("{id:guid}/history")]
    public async Task<IActionResult> GetHistory(Guid id, CancellationToken ct)
    {
        var userId = _currentUser.GetUserId();
        var history = await _apiKeyService.GetHistoryAsync(id, userId, ct);
        return Ok(history);
    }

    [RequirePermission("settings:read")]
    [HttpGet("/api/organizations/{orgId:guid}/api-keys")]
    public async Task<IActionResult> ListByOrganization(Guid orgId, CancellationToken ct)
    {
        var userId = _currentUser.GetUserId();
        if (!_currentUser.IsAdmin())
        {
            var hasPermission = await _orgMembership.HasPermissionAsync(userId, orgId, Permissions.ApiKeyAdmin, ct);
            if (!hasPermission) return Forbid();
        }

        var keys = await _apiKeyService.ListByOrganizationAsync(orgId, ct);
        return Ok(keys.Select(ToDto));
    }

    private static ApiKeyDto ToDto(ApiKeyCredential k) => new(
        k.Id, k.Label, k.Provider.ToString(), k.EnvVarName,
        k.MaskedValue ?? "****", k.IsValid,
        k.LastValidatedAt, k.LastUsedAt, k.CreatedAt, k.UpdatedAt, k.BaseUrl, k.ModelName);
}

public record CreateApiKeyDto
{
    public required string Label { get; init; }
    public required string Provider { get; init; }
    public required string ApiKey { get; init; }
    public string? EnvVarName { get; init; }
    public Guid? OrganizationId { get; init; }
    public string? BaseUrl { get; init; }
    public string? ModelName { get; init; }
}

public record UpdateApiKeyDto
{
    public string? Label { get; init; }
    public string? ApiKey { get; init; }
    public string? BaseUrl { get; init; }
    public string? ModelName { get; init; }
}

public record ApiKeyDto(
    Guid Id, string Label, string Provider, string EnvVarName,
    string MaskedValue, bool IsValid,
    DateTime? LastValidatedAt, DateTime? LastUsedAt,
    DateTime CreatedAt, DateTime? UpdatedAt, string? BaseUrl, string? ModelName);
