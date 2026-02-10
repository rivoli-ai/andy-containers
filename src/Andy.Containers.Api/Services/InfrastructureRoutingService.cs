using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Services;

public class InfrastructureRoutingService : IInfrastructureRoutingService
{
    private readonly ContainersDbContext _db;

    public InfrastructureRoutingService(ContainersDbContext db)
    {
        _db = db;
    }

    public async Task<Models.InfrastructureProvider> SelectProviderAsync(
        ContainerSpec spec, RoutingPreferences? preferences, CancellationToken ct)
    {
        var candidates = await GetCandidateProvidersAsync(spec, ct);
        if (candidates.Count == 0)
            throw new InvalidOperationException("No available infrastructure provider matches the request");

        // Prefer by preferences
        if (preferences?.PreferredProviderId is not null)
        {
            var preferred = candidates.FirstOrDefault(c => c.Provider.Id == preferences.PreferredProviderId);
            if (preferred is not null) return preferred.Provider;
        }

        if (preferences?.PreferredProviderType is not null)
        {
            var byType = candidates.FirstOrDefault(c => c.Provider.Type == preferences.PreferredProviderType);
            if (byType is not null) return byType.Provider;
        }

        return candidates[0].Provider;
    }

    public async Task<IReadOnlyList<ProviderCandidate>> GetCandidateProvidersAsync(
        ContainerSpec spec, CancellationToken ct)
    {
        var providers = await _db.Providers
            .Where(p => p.IsEnabled)
            .ToListAsync(ct);

        var candidates = providers
            .Select(p => new ProviderCandidate
            {
                Provider = p,
                SuitabilityScore = ScoreProvider(p, spec),
                MeetsGpuRequirement = spec.Gpu is null || !spec.Gpu.Required || HasGpu(p),
                MeetsResourceRequirement = true
            })
            .Where(c => c.MeetsGpuRequirement)
            .OrderByDescending(c => c.SuitabilityScore)
            .ToList();

        return candidates;
    }

    private static double ScoreProvider(Models.InfrastructureProvider provider, ContainerSpec spec)
    {
        double score = 50;
        if (provider.HealthStatus == ProviderHealth.Healthy) score += 20;
        if (provider.HealthStatus == ProviderHealth.Degraded) score -= 10;
        if (provider.HealthStatus == ProviderHealth.Unreachable) score -= 100;
        if (provider.Type == ProviderType.AppleContainer) score += 5; // Prefer native
        if (provider.Type == ProviderType.Docker) score += 3;
        return score;
    }

    private static bool HasGpu(Models.InfrastructureProvider provider)
    {
        return provider.Capabilities?.Contains("\"supportsGpu\":true", StringComparison.OrdinalIgnoreCase) ?? false;
    }
}
