namespace Andy.Containers.Models;

public class UserSshKey
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public required string Label { get; set; }
    public required string PublicKey { get; set; }
    public required string Fingerprint { get; set; }
    public required string KeyType { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
}
