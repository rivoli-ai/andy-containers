using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Controllers;

[ApiController]
[Route("api/ssh-keys")]
[Authorize]
public class SshKeysController : ControllerBase
{
    private readonly ISshKeyService _sshKeyService;
    private readonly ICurrentUserService _currentUser;
    private readonly ContainersDbContext _db;

    public SshKeysController(ISshKeyService sshKeyService, ICurrentUserService currentUser, ContainersDbContext db)
    {
        _sshKeyService = sshKeyService;
        _currentUser = currentUser;
        _db = db;
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

    [HttpGet("config-snippet")]
    public async Task<IActionResult> GetConfigSnippet([FromQuery] Guid containerId, CancellationToken ct)
    {
        var container = await _db.Containers
            .Include(c => c.Provider)
            .FirstOrDefaultAsync(c => c.Id == containerId, ct);

        if (container is null)
            return NotFound(new { error = "Container not found" });

        if (!container.SshEnabled)
            return BadRequest(new { error = "SSH is not enabled on this container" });

        var host = "localhost";
        if (!string.IsNullOrEmpty(container.IdeEndpoint))
        {
            try { host = new Uri(container.IdeEndpoint).Host; }
            catch { /* keep localhost */ }
        }

        var port = 22;
        var user = "dev";

        var snippet = $"Host container-{container.Name}\n  HostName {host}\n  Port {port}\n  User {user}\n  IdentityFile ~/.ssh/id_ed25519";

        return Ok(new { configSnippet = snippet, host, port, user, containerName = container.Name });
    }
}

public class AddSshKeyRequest
{
    public required string Label { get; set; }
    public required string PublicKey { get; set; }
}
