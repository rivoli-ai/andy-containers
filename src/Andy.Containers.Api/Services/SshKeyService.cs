using System.Security.Cryptography;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

public class SshKeyService : ISshKeyService
{
    private const int MaxKeysPerUser = 20;
    private static readonly string[] ValidKeyPrefixes =
        ["ssh-rsa", "ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521"];

    private readonly ContainersDbContext _db;
    private readonly ILogger<SshKeyService> _logger;

    public SshKeyService(ContainersDbContext db, ILogger<SshKeyService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public bool IsValidPublicKey(string publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey)) return false;
        if (publicKey.Contains("-----BEGIN")) return false;

        var trimmed = publicKey.Trim();
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        return ValidKeyPrefixes.Any(prefix => parts[0] == prefix);
    }

    public string ComputeFingerprint(string publicKey)
    {
        var parts = publicKey.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException("Invalid public key format");

        var keyBytes = Convert.FromBase64String(parts[1]);
        var hashBytes = SHA256.HashData(keyBytes);
        return $"SHA256:{Convert.ToBase64String(hashBytes).TrimEnd('=')}";
    }

    public string DetectKeyType(string publicKey)
    {
        var prefix = publicKey.Trim().Split(' ')[0];
        return prefix switch
        {
            "ssh-rsa" => "rsa",
            "ssh-ed25519" => "ed25519",
            _ when prefix.StartsWith("ecdsa-") => "ecdsa",
            _ => "unknown"
        };
    }

    public async Task<UserSshKey> AddKeyAsync(string userId, string label, string publicKey, CancellationToken ct = default)
    {
        if (!IsValidPublicKey(publicKey))
            throw new ArgumentException("Invalid SSH public key format");

        var count = await _db.SshKeys.CountAsync(k => k.UserId == userId, ct);
        if (count >= MaxKeysPerUser)
            throw new InvalidOperationException($"Maximum of {MaxKeysPerUser} SSH keys per user");

        var fingerprint = ComputeFingerprint(publicKey);

        var existing = await _db.SshKeys.FirstOrDefaultAsync(
            k => k.UserId == userId && k.Fingerprint == fingerprint, ct);
        if (existing is not null)
            throw new InvalidOperationException("SSH key with this fingerprint already exists");

        var key = new UserSshKey
        {
            UserId = userId,
            Label = label,
            PublicKey = publicKey.Trim(),
            Fingerprint = fingerprint,
            KeyType = DetectKeyType(publicKey)
        };

        _db.SshKeys.Add(key);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("SSH key registered {Fingerprint} ({KeyType}) for user {UserId}",
            fingerprint, key.KeyType, userId);

        return key;
    }

    public async Task<IReadOnlyList<UserSshKey>> ListKeysAsync(string userId, CancellationToken ct = default)
    {
        return await _db.SshKeys
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<bool> RemoveKeyAsync(string userId, Guid keyId, CancellationToken ct = default)
    {
        var key = await _db.SshKeys.FirstOrDefaultAsync(
            k => k.Id == keyId && k.UserId == userId, ct);
        if (key is null) return false;

        _logger.LogInformation("SSH key removed {Fingerprint} ({KeyType}) for user {UserId}",
            key.Fingerprint, key.KeyType, userId);

        _db.SshKeys.Remove(key);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task UpdateLastUsedAsync(string userId, IReadOnlyList<Guid> keyIds, CancellationToken ct = default)
    {
        if (keyIds.Count == 0) return;

        var keys = await _db.SshKeys
            .Where(k => k.UserId == userId && keyIds.Contains(k.Id))
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var key in keys)
        {
            key.LastUsedAt = now;
        }

        if (keys.Count > 0)
            await _db.SaveChangesAsync(ct);
    }
}
