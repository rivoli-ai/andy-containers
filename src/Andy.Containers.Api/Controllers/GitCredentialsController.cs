using Andy.Containers.Api.Services;
using Andy.Containers.Models;
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
        var credentials = await _credentialService.ListAsync(_currentUser.GetUserId(), ct);
        var result = credentials.Select(c => new GitCredentialDto(c.Id, c.Label, c.GitHost, c.CredentialType.ToString(), c.CreatedAt, c.LastUsedAt));
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateGitCredentialDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Label) || string.IsNullOrWhiteSpace(dto.Token))
            return BadRequest(new { error = "Label and token are required" });

        var type = GitCredentialType.PersonalAccessToken;
        if (!string.IsNullOrEmpty(dto.CredentialType) && !Enum.TryParse(dto.CredentialType, true, out type))
            return BadRequest(new { error = "Invalid credential type" });

        var credential = await _credentialService.CreateAsync(
            _currentUser.GetUserId(), dto.Label, dto.Token, dto.GitHost, type, ct);

        return CreatedAtAction(nameof(List), null,
            new GitCredentialDto(credential.Id, credential.Label, credential.GitHost, credential.CredentialType.ToString(), credential.CreatedAt, credential.LastUsedAt));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var deleted = await _credentialService.DeleteAsync(id, _currentUser.GetUserId(), ct);
        if (!deleted) return NotFound();
        return NoContent();
    }
}

public record GitCredentialDto(Guid Id, string Label, string? GitHost, string CredentialType, DateTime CreatedAt, DateTime? LastUsedAt);
public record CreateGitCredentialDto(string Label, string Token, string? GitHost = null, string? CredentialType = null);
