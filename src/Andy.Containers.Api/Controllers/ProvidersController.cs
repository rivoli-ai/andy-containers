using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Andy.Rbac.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ProvidersController : ControllerBase
{
    private readonly ContainersDbContext _db;
    private readonly IInfrastructureProviderFactory _providerFactory;
    private readonly ICostEstimationService _costService;
    private readonly ICurrentUserService _currentUser;
    private readonly IOrganizationMembershipService _orgMembership;

    public ProvidersController(
        ContainersDbContext db,
        IInfrastructureProviderFactory providerFactory,
        ICostEstimationService costService,
        ICurrentUserService currentUser,
        IOrganizationMembershipService orgMembership)
    {
        _db = db;
        _providerFactory = providerFactory;
        _costService = costService;
        _currentUser = currentUser;
        _orgMembership = orgMembership;
    }

    [RequirePermission("provider:read")]
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? organizationId = null, CancellationToken ct = default)
    {
        if (organizationId.HasValue && !_currentUser.IsAdmin())
        {
            var hasPermission = await _orgMembership.HasPermissionAsync(
                _currentUser.GetUserId(), organizationId.Value, Permissions.ProviderRead, ct);
            if (!hasPermission) return Forbid();
        }

        var query = _db.Providers.AsQueryable();
        if (organizationId.HasValue)
            query = query.Where(p => p.OrganizationId == null || p.OrganizationId == organizationId);

        var providers = await query.OrderBy(p => p.Name).ToListAsync(ct);
        return Ok(providers);
    }

    [RequirePermission("provider:read")]
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var provider = await _db.Providers.FindAsync([id], ct);
        return provider is null ? NotFound() : Ok(provider);
    }

    [RequirePermission("provider:manage")]
    [HttpPost]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> Create([FromBody] InfrastructureProvider provider, CancellationToken ct)
    {
        _db.Providers.Add(provider);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = provider.Id }, provider);
    }

    [RequirePermission("provider:read")]
    [HttpGet("{id:guid}/health")]
    public async Task<IActionResult> HealthCheck(Guid id, CancellationToken ct)
    {
        var provider = await _db.Providers.FindAsync([id], ct);
        if (provider is null) return NotFound();

        try
        {
            var infra = _providerFactory.GetProvider(provider);
            var health = await infra.HealthCheckAsync(ct);
            provider.HealthStatus = health;
            provider.LastHealthCheck = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            var capabilities = await infra.GetCapabilitiesAsync(ct);
            return Ok(new { status = health, capabilities });
        }
        catch (Exception ex)
        {
            provider.HealthStatus = ProviderHealth.Unreachable;
            provider.LastHealthCheck = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return Ok(new { status = ProviderHealth.Unreachable, error = ex.Message });
        }
    }

    [RequirePermission("provider:read")]
    [HttpGet("{id:guid}/cost-estimate")]
    public async Task<IActionResult> CostEstimate(
        Guid id,
        [FromQuery] double cpuCores = 2,
        [FromQuery] int memoryMb = 4096,
        [FromQuery] int diskGb = 20,
        CancellationToken ct = default)
    {
        var provider = await _db.Providers.FindAsync([id], ct);
        if (provider is null) return NotFound();

        var resources = new ResourceSpec
        {
            CpuCores = cpuCores,
            MemoryMb = memoryMb,
            DiskGb = diskGb
        };

        var estimate = _costService.Estimate(provider.Type, resources, provider.Region);
        return Ok(estimate);
    }

    [RequirePermission("provider:manage")]
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var provider = await _db.Providers.FindAsync([id], ct);
        if (provider is null) return NotFound();
        _db.Providers.Remove(provider);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
