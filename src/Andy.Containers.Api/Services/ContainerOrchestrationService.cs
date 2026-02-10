using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ConnectionInfo = Andy.Containers.Abstractions.ConnectionInfo;

namespace Andy.Containers.Api.Services;

public class ContainerOrchestrationService : IContainerService
{
    private readonly ContainersDbContext _db;
    private readonly IInfrastructureRoutingService _routing;
    private readonly IInfrastructureProviderFactory _providerFactory;
    private readonly ILogger<ContainerOrchestrationService> _logger;

    public ContainerOrchestrationService(
        ContainersDbContext db,
        IInfrastructureRoutingService routing,
        IInfrastructureProviderFactory providerFactory,
        ILogger<ContainerOrchestrationService> logger)
    {
        _db = db;
        _routing = routing;
        _providerFactory = providerFactory;
        _logger = logger;
    }

    public async Task<Container> CreateContainerAsync(CreateContainerRequest request, CancellationToken ct)
    {
        // Resolve template
        var template = request.TemplateId.HasValue
            ? await _db.Templates.FindAsync([request.TemplateId.Value], ct)
            : await _db.Templates.FirstOrDefaultAsync(t => t.Code == request.TemplateCode, ct);

        if (template is null)
            throw new ArgumentException("Template not found");

        // Resolve or route to provider
        InfrastructureProvider provider;
        if (request.ProviderId.HasValue)
        {
            provider = await _db.Providers.FindAsync([request.ProviderId.Value], ct)
                ?? throw new ArgumentException("Provider not found");
        }
        else if (!string.IsNullOrEmpty(request.ProviderCode))
        {
            provider = await _db.Providers.FirstOrDefaultAsync(p => p.Code == request.ProviderCode, ct)
                ?? throw new ArgumentException("Provider not found");
        }
        else
        {
            var spec = new ContainerSpec
            {
                ImageReference = template.BaseImage,
                Name = request.Name,
                Resources = request.Resources,
                Gpu = request.Gpu
            };
            provider = await _routing.SelectProviderAsync(spec, new RoutingPreferences
            {
                OrganizationId = request.OrganizationId
            }, ct);
        }

        var container = new Container
        {
            Name = request.Name,
            TemplateId = template.Id,
            ProviderId = provider.Id,
            OwnerId = "system", // TODO: from auth context
            OrganizationId = request.OrganizationId,
            TeamId = request.TeamId,
            Status = ContainerStatus.Pending,
            ExpiresAt = request.ExpiresAfter.HasValue
                ? DateTime.UtcNow.Add(request.ExpiresAfter.Value)
                : null
        };

        if (request.GitRepository is not null)
        {
            container.GitRepository = System.Text.Json.JsonSerializer.Serialize(request.GitRepository);
        }

        _db.Containers.Add(container);
        _db.Events.Add(new ContainerEvent
        {
            ContainerId = container.Id,
            EventType = ContainerEventType.Created,
            SubjectId = container.OwnerId
        });

        await _db.SaveChangesAsync(ct);

        // Provision asynchronously
        _ = Task.Run(async () =>
        {
            try
            {
                container.Status = ContainerStatus.Creating;
                await _db.SaveChangesAsync(CancellationToken.None);

                var infra = _providerFactory.GetProvider(provider);
                var spec = new ContainerSpec
                {
                    ImageReference = template.BaseImage,
                    Name = container.Name,
                    Resources = request.Resources ?? new ResourceSpec(),
                    Gpu = request.Gpu
                };

                var result = await infra.CreateContainerAsync(spec, CancellationToken.None);
                container.ExternalId = result.ExternalId;
                container.Status = ContainerStatus.Running;
                container.StartedAt = DateTime.UtcNow;

                if (result.ConnectionInfo is not null)
                {
                    container.IdeEndpoint = result.ConnectionInfo.IdeEndpoint;
                    container.VncEndpoint = result.ConnectionInfo.VncEndpoint;
                    container.NetworkConfig = System.Text.Json.JsonSerializer.Serialize(result.ConnectionInfo);
                }

                _db.Events.Add(new ContainerEvent
                {
                    ContainerId = container.Id,
                    EventType = ContainerEventType.Started,
                    SubjectId = container.OwnerId
                });

                await _db.SaveChangesAsync(CancellationToken.None);
                _logger.LogInformation("Container {ContainerId} provisioned on {Provider}", container.Id, provider.Code);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to provision container {ContainerId}", container.Id);
                container.Status = ContainerStatus.Failed;
                _db.Events.Add(new ContainerEvent
                {
                    ContainerId = container.Id,
                    EventType = ContainerEventType.Failed,
                    Details = System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message })
                });
                await _db.SaveChangesAsync(CancellationToken.None);
            }
        }, ct);

        return container;
    }

    public async Task<Container> GetContainerAsync(Guid containerId, CancellationToken ct)
    {
        return await _db.Containers
            .Include(c => c.Template)
            .Include(c => c.Provider)
            .FirstOrDefaultAsync(c => c.Id == containerId, ct)
            ?? throw new KeyNotFoundException($"Container {containerId} not found");
    }

    public async Task<IReadOnlyList<Container>> ListContainersAsync(ContainerFilter filter, CancellationToken ct)
    {
        var query = _db.Containers
            .Include(c => c.Template)
            .Include(c => c.Provider)
            .AsQueryable();

        if (!string.IsNullOrEmpty(filter.OwnerId))
            query = query.Where(c => c.OwnerId == filter.OwnerId);
        if (filter.OrganizationId.HasValue)
            query = query.Where(c => c.OrganizationId == filter.OrganizationId);
        if (filter.TeamId.HasValue)
            query = query.Where(c => c.TeamId == filter.TeamId);
        if (filter.WorkspaceId.HasValue)
            query = query.Where(c => _db.Workspaces.Any(w => w.Id == filter.WorkspaceId && w.Containers.Contains(c)));
        if (filter.Status.HasValue)
            query = query.Where(c => c.Status == filter.Status);
        if (filter.TemplateId.HasValue)
            query = query.Where(c => c.TemplateId == filter.TemplateId);
        if (filter.ProviderId.HasValue)
            query = query.Where(c => c.ProviderId == filter.ProviderId);

        query = query.OrderByDescending(c => c.CreatedAt);

        if (filter.Skip.HasValue)
            query = query.Skip(filter.Skip.Value);
        if (filter.Take.HasValue)
            query = query.Take(filter.Take.Value);
        else
            query = query.Take(20);

        return await query.ToListAsync(ct);
    }

    public async Task StartContainerAsync(Guid containerId, CancellationToken ct)
    {
        var container = await GetContainerAsync(containerId, ct);
        if (container.Status != ContainerStatus.Stopped)
            throw new InvalidOperationException($"Container is {container.Status}, cannot start");

        var infra = _providerFactory.GetProvider(container.Provider!);
        await infra.StartContainerAsync(container.ExternalId!, ct);

        container.Status = ContainerStatus.Running;
        container.StartedAt = DateTime.UtcNow;
        container.StoppedAt = null;
        _db.Events.Add(new ContainerEvent { ContainerId = containerId, EventType = ContainerEventType.Started });
        await _db.SaveChangesAsync(ct);
    }

    public async Task StopContainerAsync(Guid containerId, CancellationToken ct)
    {
        var container = await GetContainerAsync(containerId, ct);
        if (container.Status != ContainerStatus.Running)
            throw new InvalidOperationException($"Container is {container.Status}, cannot stop");

        var infra = _providerFactory.GetProvider(container.Provider!);
        await infra.StopContainerAsync(container.ExternalId!, ct);

        container.Status = ContainerStatus.Stopped;
        container.StoppedAt = DateTime.UtcNow;
        _db.Events.Add(new ContainerEvent { ContainerId = containerId, EventType = ContainerEventType.Stopped });
        await _db.SaveChangesAsync(ct);
    }

    public async Task DestroyContainerAsync(Guid containerId, CancellationToken ct)
    {
        var container = await GetContainerAsync(containerId, ct);
        if (container.ExternalId is not null)
        {
            var infra = _providerFactory.GetProvider(container.Provider!);
            await infra.DestroyContainerAsync(container.ExternalId, ct);
        }

        container.Status = ContainerStatus.Destroyed;
        _db.Events.Add(new ContainerEvent { ContainerId = containerId, EventType = ContainerEventType.Destroyed });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<ExecResult> ExecAsync(Guid containerId, string command, CancellationToken ct)
    {
        var container = await GetContainerAsync(containerId, ct);
        if (container.Status != ContainerStatus.Running)
            throw new InvalidOperationException($"Container is {container.Status}, cannot exec");

        var infra = _providerFactory.GetProvider(container.Provider!);
        return await infra.ExecAsync(container.ExternalId!, command, ct);
    }

    public async Task<ConnectionInfo> GetConnectionInfoAsync(Guid containerId, CancellationToken ct)
    {
        var container = await GetContainerAsync(containerId, ct);
        if (container.ExternalId is null)
            return new ConnectionInfo();

        var infra = _providerFactory.GetProvider(container.Provider!);
        return await infra.GetConnectionInfoAsync(container.ExternalId, ct);
    }
}
