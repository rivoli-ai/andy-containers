using Andy.Containers.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andy.Containers.Api.Controllers;

[ApiController]
[Route("api/ssh-keys")]
[Authorize]
public class SshKeysController : ControllerBase
{
    private readonly ISshKeyService _sshKeyService;
    private readonly ICurrentUserService _currentUser;

    public SshKeysController(ISshKeyService sshKeyService, ICurrentUserService currentUser)
    {
        _sshKeyService = sshKeyService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var keys = await _sshKeyService.ListKeysAsync(_currentUser.GetUserId(), ct);
        var result = keys.Select(k => new
        {
            k.Id, k.Label, k.Fingerprint, k.KeyType, k.CreatedAt, k.LastUsedAt
        });
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddSshKeyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Label))
            return BadRequest(new { error = "Label is required" });

        if (string.IsNullOrWhiteSpace(request.PublicKey))
            return BadRequest(new { error = "PublicKey is required" });

        if (request.PublicKey.Contains("-----BEGIN"))
            return BadRequest(new { error = "Private keys are not accepted. Please provide a public key." });

        if (!_sshKeyService.IsValidPublicKey(request.PublicKey))
            return UnprocessableEntity(new { error = "Invalid SSH public key format" });

        try
        {
            var key = await _sshKeyService.AddKeyAsync(_currentUser.GetUserId(), request.Label, request.PublicKey, ct);
            return CreatedAtAction(nameof(List), new
            {
                key.Id, key.Label, key.Fingerprint, key.KeyType, key.CreatedAt
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        var removed = await _sshKeyService.RemoveKeyAsync(_currentUser.GetUserId(), id, ct);
        return removed ? NoContent() : NotFound();
    }
}

public class AddSshKeyRequest
{
    public required string Label { get; set; }
    public required string PublicKey { get; set; }
}
