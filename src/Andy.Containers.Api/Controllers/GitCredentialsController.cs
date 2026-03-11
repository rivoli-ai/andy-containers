using Andy.Containers.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andy.Containers.Api.Controllers;

[ApiController]
[Route("api/git-credentials")]
[Authorize]
public class GitCredentialsController : ControllerBase
{
    private readonly IGitCredentialService _credentialService;
    private readonly ICurrentUserService _currentUser;

    public GitCredentialsController(IGitCredentialService credentialService, ICurrentUserService currentUser)
    {
        _credentialService = credentialService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var credentials = await _credentialService.ListCredentialsAsync(_currentUser.GetUserId(), ct);
        return Ok(credentials);
    }

    [HttpPost]
    public async Task<IActionResult> Store([FromBody] StoreGitCredentialRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Label))
            return BadRequest(new { error = "Label is required" });
        if (string.IsNullOrWhiteSpace(request.CredentialType))
            return BadRequest(new { error = "CredentialType is required" });
        if (string.IsNullOrWhiteSpace(request.Value))
            return BadRequest(new { error = "Value is required" });

        try
        {
            var info = await _credentialService.StoreCredentialAsync(
                _currentUser.GetUserId(), request.Label, request.CredentialType,
                request.Value, request.GitHost, ct);
            return CreatedAtAction(nameof(List), info);
        }
        catch (Exception ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        var removed = await _credentialService.RemoveCredentialAsync(_currentUser.GetUserId(), id, ct);
        return removed ? NoContent() : NotFound();
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateGitCredentialRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Value))
            return BadRequest(new { error = "Value is required" });

        var updated = await _credentialService.UpdateCredentialAsync(_currentUser.GetUserId(), id, request.Value, ct);
        return updated ? NoContent() : NotFound();
    }
}

public class StoreGitCredentialRequest
{
    public required string Label { get; set; }
    public required string CredentialType { get; set; }
    public required string Value { get; set; }
    public string? GitHost { get; set; }
}

public class UpdateGitCredentialRequest
{
    public required string Value { get; set; }
}
