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
    private readonly ISshProvisioningService _sshProvisioning;
    private readonly ICurrentUserService _currentUser;
    private readonly IContainerPermissionService _permissions;
    private readonly ContainersDbContext _db;

    public SshKeysController(ISshKeyService sshKeyService, ISshProvisioningService sshProvisioning,
        ICurrentUserService currentUser, IContainerPermissionService permissions, ContainersDbContext db)
    {
        _sshKeyService = sshKeyService;
        _sshProvisioning = sshProvisioning;
        _currentUser = currentUser;
        _permissions = permissions;
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var userId = _currentUser.GetUserId();
        var keys = await _sshKeyService.ListKeysAsync(userId, ct);
        var result = keys.Select(k => new SshKeyDto
        {
            Id = k.Id,
            Label = k.Label,
            Fingerprint = k.Fingerprint,
            KeyType = k.KeyType,
            CreatedAt = k.CreatedAt,
            LastUsedAt = k.LastUsedAt
        });
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Register([FromBody] RegisterSshKeyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PublicKey))
            return BadRequest(new { error = "Public key is required" });

        if (request.PublicKey.Contains("-----BEGIN"))
            return BadRequest(new { error = "Private keys are not accepted. Please provide a public key." });

        if (!_sshKeyService.IsValidPublicKey(request.PublicKey))
            return UnprocessableEntity(new { error = "Invalid SSH public key format" });

        var userId = _currentUser.GetUserId();

        try
        {
            var key = await _sshKeyService.AddKeyAsync(userId, request.Label ?? "Unnamed", request.PublicKey, ct);
            return CreatedAtAction(nameof(List), new SshKeyDto
            {
                Id = key.Id,
                Label = key.Label,
                Fingerprint = key.Fingerprint,
                KeyType = key.KeyType,
                CreatedAt = key.CreatedAt
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Maximum"))
        {
            return UnprocessableEntity(new { error = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("fingerprint"))
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        var userId = _currentUser.GetUserId();
        var removed = await _sshKeyService.RemoveKeyAsync(userId, id, ct);
        return removed ? NoContent() : NotFound();
    }

    [HttpPost("/api/containers/{containerId:guid}/ssh-keys")]
    public async Task<IActionResult> InjectKey(Guid containerId, [FromBody] InjectSshKeyRequest request, CancellationToken ct)
    {
        var container = await _db.Containers.FindAsync([containerId], ct);
        if (container is null) return NotFound();

        var userId = _currentUser.GetUserId();
        if (!await _permissions.HasPermissionAsync(userId, containerId, "container:connect", ct))
            return Forbid();

        if (!container.SshEnabled)
            return BadRequest(new { error = "SSH is not enabled on this container" });

        if (!_sshKeyService.IsValidPublicKey(request.PublicKey))
            return UnprocessableEntity(new { error = "Invalid SSH public key format" });

        var fingerprint = _sshKeyService.ComputeFingerprint(request.PublicKey);

        // Update LastUsedAt if this matches a registered key
        var userKeys = await _sshKeyService.ListKeysAsync(userId, ct);
        var matchingKey = userKeys.FirstOrDefault(k => k.Fingerprint == fingerprint);
        if (matchingKey is not null)
        {
            await _sshKeyService.UpdateLastUsedAsync(userId, [matchingKey.Id], ct);
        }

        _db.Events.Add(new ContainerEvent
        {
            ContainerId = containerId,
            EventType = ContainerEventType.SshKeyInjected,
            SubjectId = userId,
            Details = System.Text.Json.JsonSerializer.Serialize(new { fingerprint, keyType = request.PublicKey.Split(' ')[0] })
        });
        await _db.SaveChangesAsync(ct);

        return Ok(new { injected = true, fingerprint });
    }

    [HttpGet("/api/containers/{containerId:guid}/ssh-config")]
    public async Task<IActionResult> GetSshConfig(Guid containerId, CancellationToken ct)
    {
        var container = await _db.Containers.FindAsync([containerId], ct);
        if (container is null) return NotFound();

        var userId = _currentUser.GetUserId();
        if (!await _permissions.HasPermissionAsync(userId, containerId, "container:connect", ct))
            return Forbid();

        if (!container.SshEnabled)
            return BadRequest(new { error = "SSH is not enabled on this container" });

        var shortId = container.Id.ToString()[..8];
        var configSnippet = $"""
            Host andy-container-{shortId}
              HostName localhost
              Port 22
              User dev
              IdentityFile ~/.ssh/id_ed25519
              StrictHostKeyChecking no
              UserKnownHostsFile /dev/null
            """;

        return Ok(new { sshEnabled = true, host = "localhost", port = 22, username = "dev", configSnippet });
    }

}

public class RegisterSshKeyRequest
{
    public string? Label { get; set; }
    public required string PublicKey { get; set; }
}

public class InjectSshKeyRequest
{
    public required string PublicKey { get; set; }
}

public class SshKeyDto
{
    public Guid Id { get; set; }
    public required string Label { get; set; }
    public required string Fingerprint { get; set; }
    public required string KeyType { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}
