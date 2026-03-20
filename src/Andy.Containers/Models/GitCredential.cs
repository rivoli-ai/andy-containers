namespace Andy.Containers.Models;

public class GitCredential
{
    public Guid Id { get; set; }
    public required string OwnerId { get; set; }
    public required string Label { get; set; }
    public string? GitHost { get; set; }
    public GitCredentialType CredentialType { get; set; } = GitCredentialType.PersonalAccessToken;
    public required string EncryptedToken { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
}

public enum GitCredentialType
{
    PersonalAccessToken,
    DeployKey,
    OAuthToken
}
