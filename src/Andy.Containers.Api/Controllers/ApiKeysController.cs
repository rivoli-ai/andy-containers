using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using Andy.Rbac.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andy.Containers.Api.Controllers;

[ApiController]
[Route("api/apikeys")]
[Authorize]
public sealed class ApiKeysController : ControllerBase
{
    private readonly IApiKeyService _apiKeys;
    private readonly ICurrentUserService _currentUser;

    public ApiKeysController(
        IApiKeyService apiKeys,
        ICurrentUserService currentUser)
    {
        _apiKeys = apiKeys;
        _currentUser = currentUser;
    }

    [HttpGet]
    [RequirePermission("settings:read")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var entries = await _apiKeys.ListAsync(_currentUser.GetUserId(), ct);
        return Ok(entries.Select(ToDto));
    }

    [HttpPost]
    [RequirePermission("settings:write")]
    public async Task<IActionResult> Create(
        [FromBody] CreateApiKeyRequest request,
        CancellationToken ct)
    {
        try
        {
            var entry = await _apiKeys.CreateAsync(
                _currentUser.GetUserId(),
                new CreateApiKeyCommand(
                    request.Name,
                    request.Provider,
                    request.Value,
                    request.Model,
                    request.BaseURL),
                ct);
            return StatusCode(StatusCodes.Status201Created, ToDto(entry));
        }
        catch (ApiKeyValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (ApiKeyConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (ApiKeySecretStoreUnavailableException ex)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    [RequirePermission("settings:write")]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateApiKeyRequest request,
        CancellationToken ct)
    {
        try
        {
            var entry = await _apiKeys.UpdateAsync(
                id,
                _currentUser.GetUserId(),
                new UpdateApiKeyCommand(
                    request.Name,
                    request.Value,
                    request.Model,
                    request.BaseURL),
                ct);
            return Ok(ToDto(entry));
        }
        catch (ApiKeyNotFoundException)
        {
            return NotFound();
        }
        catch (ApiKeyValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (ApiKeySecretStoreUnavailableException ex)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission("settings:write")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        try
        {
            await _apiKeys.DeleteAsync(id, _currentUser.GetUserId(), ct);
            return NoContent();
        }
        catch (ApiKeyNotFoundException)
        {
            return NotFound();
        }
        catch (ApiKeySecretStoreUnavailableException ex)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = ex.Message });
        }
    }

    [HttpPost("{id:guid}/validate")]
    [RequirePermission("settings:write")]
    public async Task<IActionResult> Validate(Guid id, CancellationToken ct)
    {
        try
        {
            var result = await _apiKeys.ValidateAsync(
                id,
                _currentUser.GetUserId(),
                ct);
            return Ok(new ApiKeyValidationResult(
                result.IsValid,
                result.Message,
                result.QuotaRemaining));
        }
        catch (ApiKeyNotFoundException)
        {
            return NotFound();
        }
        catch (ApiKeySecretStoreUnavailableException ex)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = ex.Message });
        }
    }

    [HttpGet("{id:guid}/history")]
    [RequirePermission("settings:read")]
    public async Task<IActionResult> History(Guid id, CancellationToken ct)
    {
        try
        {
            var history = await _apiKeys.HistoryAsync(
                id,
                _currentUser.GetUserId(),
                ct);
            return Ok(history.Select(a => new ApiKeyAuditEntry(
                a.Id,
                a.KeyId,
                a.Kind,
                a.OccurredAt,
                a.Detail)));
        }
        catch (ApiKeyNotFoundException)
        {
            return NotFound();
        }
    }

    private static ApiKeyEntry ToDto(ApiKeyRegistration entry)
        => new(
            entry.Id,
            entry.Name,
            entry.Provider,
            entry.MaskedValue,
            entry.IsValid,
            entry.CreatedAt,
            entry.LastUsedAt,
            entry.LastValidatedAt,
            entry.Model,
            entry.BaseUrl);
}

public sealed record ApiKeyEntry(
    Guid Id,
    string Name,
    string Provider,
    string MaskedValue,
    bool? IsValid,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? LastValidatedAt,
    string? Model,
    string? BaseURL);

public sealed record CreateApiKeyRequest(
    string Name,
    string Provider,
    string Value,
    string? Model = null,
    string? BaseURL = null);

public sealed record UpdateApiKeyRequest(
    string? Name = null,
    string? Value = null,
    string? Model = null,
    string? BaseURL = null);

public sealed record ApiKeyValidationResult(
    bool IsValid,
    string? Message,
    int? QuotaRemaining);

public sealed record ApiKeyAuditEntry(
    Guid Id,
    Guid KeyId,
    string Kind,
    DateTimeOffset OccurredAt,
    string? Detail);
