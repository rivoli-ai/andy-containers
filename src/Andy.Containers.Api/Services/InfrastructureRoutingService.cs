using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Services;

public class InfrastructureRoutingService : IInfrastructureRoutingService
{
    private readonly ContainersDbContext _db;
    private readonly ICostEstimationService _costService;

    public InfrastructureRoutingService(ContainersDbContext db, ICostEstimationService costService)
    {
        _db = db;
        _costService = costService;
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

        if (preferences?.PreferredRegion is not null)
        {
            var byRegion = candidates.FirstOrDefault(c =>
                string.Equals(c.Provider.Region, preferences.PreferredRegion, StringComparison.OrdinalIgnoreCase));
            if (byRegion is not null) return byRegion.Provider;
        }

        // If PreferCheapest, re-sort by cost
        if (preferences?.PreferCheapest == true)
        {
            var resources = spec.Resources ?? new ResourceSpec();
            var cheapest = candidates
                .OrderBy(c => _costService.Estimate(c.Provider.Type, resources, c.Provider.Region).HourlyCostUsd)
                .First();
            return cheapest.Provider;
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
            .Where(p => p.HealthStatus != ProviderHealth.Unreachable)
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

        // Health scoring
        if (provider.HealthStatus == ProviderHealth.Healthy) score += 20;
        if (provider.HealthStatus == ProviderHealth.Degraded) score -= 10;
        if (provider.HealthStatus == ProviderHealth.Unreachable) score -= 100;

        // Prefer local providers when available (lower latency, no cloud cost)
        if (provider.Type == ProviderType.AppleContainer) score += 5;
        if (provider.Type == ProviderType.Docker) score += 3;

        // Cloud provider scoring: prefer providers with native exec
        if (provider.Type is ProviderType.AzureAci or ProviderType.AwsFargate or ProviderType.FlyIo)
            score += 2; // Native exec support

        // Cost-effectiveness boost for budget-friendly providers
        if (provider.Type == ProviderType.Hetzner) score += 1;

        return score;
    }

    private static bool HasGpu(Models.InfrastructureProvider provider)
    {
        return provider.Capabilities?.Contains("\"supportsGpu\":true", StringComparison.OrdinalIgnoreCase) ?? false;
    }
}
