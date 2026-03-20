using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Controllers;

[ApiController]
[Route("api/organizations")]
[Authorize]
public class OrganizationsController : ControllerBase
{
    private readonly ContainersDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IOrganizationMembershipService _orgMembership;

    public OrganizationsController(
        ContainersDbContext db,
        ICurrentUserService currentUser,
        IOrganizationMembershipService orgMembership)
    {
        _db = db;
        _currentUser = currentUser;
        _orgMembership = orgMembership;
    }

    [HttpGet("{orgId:guid}/images")]
    public async Task<IActionResult> ListImages(Guid orgId, CancellationToken ct)
    {
        if (!_currentUser.IsAdmin())
        {
            var hasPermission = await _orgMembership.HasPermissionAsync(
                _currentUser.GetUserId(), orgId, Permissions.ImageRead, ct);
            if (!hasPermission) return Forbid();
        }

        var images = await _db.Images
            .Where(i => i.OrganizationId == orgId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);
        return Ok(images);
    }

    [HttpPost("{orgId:guid}/images/{imageId:guid}/publish")]
    public async Task<IActionResult> PublishImage(Guid orgId, Guid imageId, CancellationToken ct)
    {
        if (!_currentUser.IsAdmin())
        {
            var hasPermission = await _orgMembership.HasPermissionAsync(
                _currentUser.GetUserId(), orgId, Permissions.ImagePublish, ct);
            if (!hasPermission) return Forbid();
        }

        var image = await _db.Images.FirstOrDefaultAsync(
            i => i.Id == imageId && i.OrganizationId == orgId, ct);
        if (image is null) return NotFound();

        image.Visibility = ImageVisibility.Organization;
        await _db.SaveChangesAsync(ct);
        return Ok(image);
    }

    [HttpDelete("{orgId:guid}/images/{imageId:guid}")]
    public async Task<IActionResult> DeleteImage(Guid orgId, Guid imageId, CancellationToken ct)
    {
        if (!_currentUser.IsAdmin())
        {
            var hasPermission = await _orgMembership.HasPermissionAsync(
                _currentUser.GetUserId(), orgId, Permissions.ImageDelete, ct);
            if (!hasPermission) return Forbid();
        }

        var image = await _db.Images.FirstOrDefaultAsync(
            i => i.Id == imageId && i.OrganizationId == orgId, ct);
        if (image is null) return NotFound();

        _db.Images.Remove(image);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{orgId:guid}/templates")]
    public async Task<IActionResult> ListTemplates(Guid orgId, CancellationToken ct)
    {
        if (!_currentUser.IsAdmin())
        {
            var hasPermission = await _orgMembership.HasPermissionAsync(
                _currentUser.GetUserId(), orgId, Permissions.TemplateRead, ct);
            if (!hasPermission) return Forbid();
        }

        var templates = await _db.Templates
            .Where(t => t.OrganizationId == orgId || t.CatalogScope == CatalogScope.Global)
            .Where(t => t.IsPublished)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);
        return Ok(templates);
    }

    [HttpGet("{orgId:guid}/providers")]
    public async Task<IActionResult> ListProviders(Guid orgId, CancellationToken ct)
    {
        if (!_currentUser.IsAdmin())
        {
            var hasPermission = await _orgMembership.HasPermissionAsync(
                _currentUser.GetUserId(), orgId, Permissions.ProviderRead, ct);
            if (!hasPermission) return Forbid();
        }

        var providers = await _db.Providers
            .Where(p => p.OrganizationId == null || p.OrganizationId == orgId)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
        return Ok(providers);
    }
}
