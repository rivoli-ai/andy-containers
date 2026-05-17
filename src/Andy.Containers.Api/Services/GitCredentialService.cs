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
        // OT7 (rivoli-ai/conductor#1265). `gitCredential.*` → `andy.containers.git.*`.
        var hostTag = gitHost ?? "unknown";
        activity?.SetTag("andy.containers.git.host", hostTag);
        activity?.SetTag("gitCredential.host", hostTag); // deprecated; removed in 0.3.0

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

    public async Task<IReadOnlyList<DecryptedGitCredential>> ListWithDecryptedTokensAsync(
        string ownerId,
        CancellationToken ct)
    {
        using var activity = ActivitySources.Git.StartActivity("GitCredential.ListDecrypted");
        var rows = await _db.GitCredentials
            .Where(c => c.OwnerId == ownerId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        // Decrypt in-memory; failures on individual rows shouldn't drop
        // the whole bag — log + skip so a corrupted single credential
        // doesn't block the whole container provisioning step.
        var decrypted = new List<DecryptedGitCredential>(rows.Count);
        foreach (var row in rows)
        {
            string plaintext;
            try
            {
                plaintext = _protector.Unprotect(row.EncryptedToken);
            }
            catch (Exception)
            {
                // Skip and keep going. Container provisioning works
                // best-effort; one bad row mustn't fail the whole step.
                continue;
            }
            decrypted.Add(new DecryptedGitCredential(
                Id: row.Id,
                Label: row.Label,
                GitHost: row.GitHost,
                CredentialType: row.CredentialType,
                PlaintextToken: plaintext));
        }
        return decrypted;
    }

    public async Task<string?> ResolveTokenAsync(string ownerId, string? credentialRef, string? gitHost, CancellationToken ct)
    {
        using var activity = ActivitySources.Git.StartActivity("GitCredential.Resolve");
        // OT7 (rivoli-ai/conductor#1265). `gitCredential.*` → `andy.containers.git.*`.
        var hostTag = gitHost ?? "unknown";
        var hasRefTag = (!string.IsNullOrEmpty(credentialRef)).ToString();
        activity?.SetTag("andy.containers.git.host", hostTag);
        activity?.SetTag("andy.containers.git.has_credential_ref", hasRefTag);
        activity?.SetTag("gitCredential.host", hostTag);   // deprecated; removed in 0.3.0
        activity?.SetTag("gitCredential.hasRef", hasRefTag); // deprecated; removed in 0.3.0

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
