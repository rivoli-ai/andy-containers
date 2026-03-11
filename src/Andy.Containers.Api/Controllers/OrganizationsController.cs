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
    private readonly IOrganizationMembershipService _orgMembership;
    private readonly IContainerAuthorizationService _authService;
    private readonly ICurrentUserService _currentUser;

    public OrganizationsController(
        ContainersDbContext db,
        IOrganizationMembershipService orgMembership,
        IContainerAuthorizationService authService,
        ICurrentUserService currentUser)
    {
        _db = db;
        _orgMembership = orgMembership;
        _authService = authService;
        _currentUser = currentUser;
    }

    [HttpGet("{orgId:guid}/images")]
    public async Task<IActionResult> ListImages(Guid orgId, CancellationToken ct)
    {
        var userId = _currentUser.GetUserId();
        if (!await _orgMembership.IsMemberAsync(userId, orgId, ct))
            return Forbid();

        var images = await _db.Images
            .Where(i => i.OrganizationId == orgId || i.OrganizationId == null)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        return Ok(images);
    }

    [HttpPost("{orgId:guid}/images/{imageId:guid}/publish")]
    public async Task<IActionResult> PublishImage(Guid orgId, Guid imageId, CancellationToken ct)
    {
        var userId = _currentUser.GetUserId();
        if (!await _orgMembership.HasPermissionAsync(userId, orgId, "image:publish", ct))
            return Forbid();

        var image = await _db.Images.FindAsync([imageId], ct);
        if (image is null || image.OrganizationId != orgId) return NotFound();

        image.Visibility = ImageVisibility.Organization;
        await _db.SaveChangesAsync(ct);
        return Ok(image);
    }

    [HttpDelete("{orgId:guid}/images/{imageId:guid}")]
    public async Task<IActionResult> DeleteImage(Guid orgId, Guid imageId, CancellationToken ct)
    {
        var userId = _currentUser.GetUserId();
        if (!await _orgMembership.HasPermissionAsync(userId, orgId, "image:delete", ct))
            return Forbid();

        var image = await _db.Images.FindAsync([imageId], ct);
        if (image is null || image.OrganizationId != orgId) return NotFound();

        _db.Images.Remove(image);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{orgId:guid}/templates")]
    public async Task<IActionResult> ListTemplates(Guid orgId, CancellationToken ct)
    {
        var userId = _currentUser.GetUserId();
        if (!await _orgMembership.IsMemberAsync(userId, orgId, ct))
            return Forbid();

        var templates = await _db.Templates
            .Where(t => (t.OrganizationId == orgId || t.CatalogScope == CatalogScope.Global) && t.IsPublished)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        return Ok(templates);
    }

    [HttpGet("{orgId:guid}/providers")]
    public async Task<IActionResult> ListProviders(Guid orgId, CancellationToken ct)
    {
        var userId = _currentUser.GetUserId();
        if (!await _orgMembership.IsMemberAsync(userId, orgId, ct))
            return Forbid();

        var providers = await _db.Providers
            .Where(p => p.OrganizationId == orgId || p.OrganizationId == null)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        return Ok(providers);
    }
}
