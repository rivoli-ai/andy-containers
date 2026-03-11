namespace Andy.Containers.Models;

public class GitCredential
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public required string Label { get; set; }
    public required string CredentialType { get; set; }
    public required string EncryptedValue { get; set; }
    public string? GitHost { get; set; }
    public Guid? OrganizationId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
}
