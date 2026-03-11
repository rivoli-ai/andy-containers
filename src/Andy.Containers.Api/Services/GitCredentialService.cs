using System.Security.Cryptography;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Andy.Containers.Api.Services;

public class GitCredentialService : IGitCredentialService
{
    private readonly ContainersDbContext _db;
    private readonly byte[] _encryptionKey;

    public GitCredentialService(ContainersDbContext db, IConfiguration configuration)
    {
        _db = db;
        var keyBase64 = configuration["Encryption:Key"] ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _encryptionKey = Convert.FromBase64String(keyBase64);
    }

    public async Task<GitCredentialInfo> StoreCredentialAsync(string userId, string label, string credentialType, string value, string? gitHost = null, CancellationToken ct = default)
    {
        var encrypted = Encrypt(value);

        var credential = new GitCredential
        {
            UserId = userId,
            Label = label,
            CredentialType = credentialType,
            EncryptedValue = encrypted,
            GitHost = gitHost
        };

        _db.GitCredentials.Add(credential);
        await _db.SaveChangesAsync(ct);

        return new GitCredentialInfo(credential.Id, credential.Label, credential.CredentialType, credential.GitHost, credential.CreatedAt);
    }

    public async Task<IReadOnlyList<GitCredentialInfo>> ListCredentialsAsync(string userId, CancellationToken ct = default)
    {
        return await _db.GitCredentials
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new GitCredentialInfo(c.Id, c.Label, c.CredentialType, c.GitHost, c.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<bool> RemoveCredentialAsync(string userId, Guid credentialId, CancellationToken ct = default)
    {
        var credential = await _db.GitCredentials.FirstOrDefaultAsync(
            c => c.Id == credentialId && c.UserId == userId, ct);
        if (credential is null) return false;

        _db.GitCredentials.Remove(credential);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UpdateCredentialAsync(string userId, Guid credentialId, string newValue, CancellationToken ct = default)
    {
        var credential = await _db.GitCredentials.FirstOrDefaultAsync(
            c => c.Id == credentialId && c.UserId == userId, ct);
        if (credential is null) return false;

        credential.EncryptedValue = Encrypt(newValue);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private string Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_encryptionKey, 16);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Format: nonce(12) + tag(16) + ciphertext
        var result = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, nonce.Length);
        ciphertext.CopyTo(result, nonce.Length + tag.Length);

        return Convert.ToBase64String(result);
    }
}
