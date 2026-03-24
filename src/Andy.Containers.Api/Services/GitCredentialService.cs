using Andy.Containers.Api.Telemetry;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Services;

public class GitCredentialService : IGitCredentialService
{
    private readonly ContainersDbContext _db;
    private readonly IDataProtector _protector;

    private const string ProtectorPurpose = "GitCredential.Token";

    public GitCredentialService(ContainersDbContext db, IDataProtectionProvider dataProtection)
    {
        _db = db;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
    }

    public async Task<GitCredential> CreateAsync(string ownerId, string label, string token, string? gitHost, GitCredentialType type, CancellationToken ct)
    {
        using var activity = ActivitySources.Git.StartActivity("GitCredential.Create");
        activity?.SetTag("gitCredential.host", gitHost ?? "unknown");

        var credential = new GitCredential
        {
            OwnerId = ownerId,
            Label = label,
            GitHost = gitHost,
            CredentialType = type,
            EncryptedToken = _protector.Protect(token)
        };

        _db.GitCredentials.Add(credential);
        await _db.SaveChangesAsync(ct);
        return credential;
    }

    public async Task<IReadOnlyList<GitCredential>> ListAsync(string ownerId, CancellationToken ct)
    {
        return await _db.GitCredentials
            .Where(c => c.OwnerId == ownerId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid id, string ownerId, CancellationToken ct)
    {
        var credential = await _db.GitCredentials
            .FirstOrDefaultAsync(c => c.Id == id && c.OwnerId == ownerId, ct);
        if (credential is null) return false;

        _db.GitCredentials.Remove(credential);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<string?> ResolveTokenAsync(string ownerId, string? credentialRef, string? gitHost, CancellationToken ct)
    {
        using var activity = ActivitySources.Git.StartActivity("GitCredential.Resolve");
        activity?.SetTag("gitCredential.host", gitHost ?? "unknown");
        activity?.SetTag("gitCredential.hasRef", (!string.IsNullOrEmpty(credentialRef)).ToString());

        GitCredential? credential = null;

        // Try label match first
        if (!string.IsNullOrEmpty(credentialRef))
        {
            credential = await _db.GitCredentials
                .FirstOrDefaultAsync(c => c.OwnerId == ownerId && c.Label == credentialRef, ct);
        }

        // Fallback: auto-match by host
        if (credential is null && !string.IsNullOrEmpty(gitHost))
        {
            credential = await _db.GitCredentials
                .FirstOrDefaultAsync(c => c.OwnerId == ownerId && c.GitHost == gitHost, ct);
        }

        if (credential is null) return null;

        // Update last used
        credential.LastUsedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return _protector.Unprotect(credential.EncryptedToken);
    }
}
