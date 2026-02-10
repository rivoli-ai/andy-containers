using Andy.Containers.Models;

namespace Andy.Containers.Abstractions;

/// <summary>
/// Selects the optimal infrastructure provider for a container request
/// based on capabilities, affinity, capacity, cost, and latency.
/// </summary>
public interface IInfrastructureRoutingService
{
    /// <summary>
    /// Select the best provider for the given container spec and preferences.
    /// </summary>
    Task<Models.InfrastructureProvider> SelectProviderAsync(
        ContainerSpec spec,
        RoutingPreferences? preferences = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get all candidate providers that could serve the request, ranked by suitability.
    /// </summary>
    Task<IReadOnlyList<ProviderCandidate>> GetCandidateProvidersAsync(
        ContainerSpec spec,
        CancellationToken ct = default);
}

public class RoutingPreferences
{
    public Guid? PreferredProviderId { get; set; }
    public ProviderType? PreferredProviderType { get; set; }
    public string? PreferredRegion { get; set; }
    public Guid? OrganizationId { get; set; }
    public bool PreferCheapest { get; set; }
    public bool PreferLowestLatency { get; set; }
}

public class ProviderCandidate
{
    public required Models.InfrastructureProvider Provider { get; set; }
    public double SuitabilityScore { get; set; }
    public string? Reason { get; set; }
    public bool MeetsGpuRequirement { get; set; }
    public bool MeetsResourceRequirement { get; set; }
}
