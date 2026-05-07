namespace Andy.Containers.Abstractions.Images;

/// <summary>
/// Mints short-lived pull credentials for workspace launches. The
/// orchestrator (DockerProvider / AppleContainersProvider /
/// KubernetesInfrastructureProvider) calls this when starting a
/// workspace so the runtime can pull the workspace's image without
/// long-lived shared registry credentials being baked into the host.
/// </summary>
public interface IPullCredentialBroker
{
    /// <summary>
    /// Mint a pull credential scoped to a single repo path on a single
    /// registry. Callers should request only the TTL they actually need;
    /// implementations may cap to lower than the requested TTL based on
    /// registry policy (e.g. ECR's 12 h maximum, Artifactory's project
    /// token expiry).
    /// </summary>
    Task<WorkspacePullCredential> MintAsync(
        string registryId,
        string repoPath,
        TimeSpan ttl,
        CancellationToken ct);
}

/// <summary>
/// A pull credential ready to be handed to a container runtime.
/// </summary>
/// <param name="RegistryId">Registry id from <see cref="IRegistryConfiguration"/>.</param>
/// <param name="RepoPath">Repo path the credential authorises pulling from.</param>
/// <param name="DockerConfigJson">
/// The credential serialised in <c>~/.docker/config.json</c> format —
/// the form Docker, containerd, and Apple Containers all accept. The
/// orchestrator writes this directly into the runtime's auth store.
/// </param>
/// <param name="ExpiresAt">When the credential stops working.</param>
public sealed record WorkspacePullCredential(
    string RegistryId,
    string RepoPath,
    string DockerConfigJson,
    DateTimeOffset ExpiresAt);
