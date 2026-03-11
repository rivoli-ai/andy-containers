using System.ComponentModel;
using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Andy.Containers.Api.Mcp;

[McpServerToolType]
public class ContainersMcpTools
{
    private readonly ContainersDbContext _db;
    private readonly IOrganizationMembershipService _orgMembership;

    public ContainersMcpTools(ContainersDbContext db, IOrganizationMembershipService orgMembership)
    {
        _db = db;
        _orgMembership = orgMembership;
    }

    [McpServerTool, Description("List all containers with their status")]
    public async Task<IReadOnlyList<McpContainerInfo>> ListContainers(
        [Description("Filter by status: Pending, Creating, Running, Stopped, Failed, Destroyed")] string? status = null)
    {
        var query = _db.Containers.Include(c => c.Template).Include(c => c.Provider).AsQueryable();
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<ContainerStatus>(status, true, out var s))
            query = query.Where(c => c.Status == s);
        var containers = await query.OrderByDescending(c => c.CreatedAt).Take(50).ToListAsync();
        return containers.Select(c => new McpContainerInfo(c.Id, c.Name, c.Template?.Name ?? "", c.Provider?.Name ?? "", c.Status.ToString(), c.IdeEndpoint, c.VncEndpoint, c.CreatedAt)).ToList();
    }

    [McpServerTool, Description("Get detailed info about a specific container")]
    public async Task<McpContainerDetail?> GetContainer([Description("Container ID (GUID)")] string containerId)
    {
        if (!Guid.TryParse(containerId, out var id)) return null;
        var c = await _db.Containers.Include(c => c.Template).Include(c => c.Provider).FirstOrDefaultAsync(c => c.Id == id);
        if (c is null) return null;
        return new McpContainerDetail(c.Id, c.Name, c.Template?.Name ?? "", c.Template?.Code ?? "", c.Provider?.Name ?? "", c.Provider?.Type.ToString() ?? "", c.Status.ToString(), c.OwnerId, c.IdeEndpoint, c.VncEndpoint, c.ExternalId, c.CreatedAt, c.StartedAt, c.StoppedAt, c.ExpiresAt);
    }

    [McpServerTool, Description("Browse the container template catalog")]
    public async Task<IReadOnlyList<McpTemplateInfo>> BrowseTemplates(
        [Description("Search by name or description")] string? search = null,
        [Description("Filter by scope: Global, Organization, Team, User")] string? scope = null)
    {
        var query = _db.Templates.Where(t => t.IsPublished).AsQueryable();
        if (!string.IsNullOrEmpty(search))
            query = query.Where(t => t.Name.Contains(search) || (t.Description != null && t.Description.Contains(search)));
        if (!string.IsNullOrEmpty(scope) && Enum.TryParse<CatalogScope>(scope, true, out var s))
            query = query.Where(t => t.CatalogScope == s);
        var templates = await query.OrderBy(t => t.Name).Take(50).ToListAsync();
        return templates.Select(t => new McpTemplateInfo(t.Id, t.Code, t.Name, t.Description ?? "", t.Version, t.CatalogScope.ToString(), t.IdeType.ToString(), t.GpuRequired, t.GpuPreferred, t.Tags ?? [])).ToList();
    }

    [McpServerTool, Description("List infrastructure providers and their health status")]
    public async Task<IReadOnlyList<McpProviderInfo>> ListProviders()
    {
        var providers = await _db.Providers.OrderBy(p => p.Name).ToListAsync();
        return providers.Select(p => new McpProviderInfo(p.Id, p.Code, p.Name, p.Type.ToString(), p.Region ?? "", p.IsEnabled, p.HealthStatus.ToString(), p.LastHealthCheck)).ToList();
    }

    [McpServerTool, Description("List workspaces")]
    public async Task<IReadOnlyList<McpWorkspaceInfo>> ListWorkspaces()
    {
        var workspaces = await _db.Workspaces.OrderByDescending(w => w.CreatedAt).Take(50).ToListAsync();
        return workspaces.Select(w => new McpWorkspaceInfo(w.Id, w.Name, w.Description ?? "", w.Status.ToString(), w.GitRepositoryUrl ?? "", w.GitBranch ?? "", w.CreatedAt)).ToList();
    }

    [McpServerTool, Description("List built images for a template")]
    public async Task<IReadOnlyList<McpImageInfo>> ListImages([Description("Template code (e.g., full-stack)")] string templateCode)
    {
        var template = await _db.Templates.FirstOrDefaultAsync(t => t.Code == templateCode);
        if (template is null) return [];
        var images = await _db.Images.Where(i => i.TemplateId == template.Id).OrderByDescending(i => i.BuildNumber).Take(20).ToListAsync();
        return images.Select(i => new McpImageInfo(i.Id, i.Tag, i.ContentHash, i.BuildNumber, i.BuildStatus.ToString(), i.BuiltOffline, i.Changelog ?? "", i.CreatedAt)).ToList();
    }
}

public record McpContainerInfo(Guid Id, string Name, string Template, string Provider, string Status, string? IdeEndpoint, string? VncEndpoint, DateTime CreatedAt);
public record McpContainerDetail(Guid Id, string Name, string TemplateName, string TemplateCode, string ProviderName, string ProviderType, string Status, string OwnerId, string? IdeEndpoint, string? VncEndpoint, string? ExternalId, DateTime CreatedAt, DateTime? StartedAt, DateTime? StoppedAt, DateTime? ExpiresAt);
public record McpTemplateInfo(Guid Id, string Code, string Name, string Description, string Version, string CatalogScope, string IdeType, bool GpuRequired, bool GpuPreferred, string[] Tags);
public record McpProviderInfo(Guid Id, string Code, string Name, string Type, string Region, bool IsEnabled, string HealthStatus, DateTime? LastHealthCheck);
public record McpWorkspaceInfo(Guid Id, string Name, string Description, string Status, string GitRepositoryUrl, string GitBranch, DateTime CreatedAt);
    // === Story 2: Organization RBAC MCP Tools ===

    [McpServerTool, Description("List images scoped to an organization")]
    public async Task<IReadOnlyList<McpOrgImageInfo>> ListOrganizationImages(
        [Description("Organization ID (GUID)")] string organizationId,
        [Description("User ID for access check")] string userId)
    {
        if (!Guid.TryParse(organizationId, out var orgId)) return [];
        if (!await _orgMembership.IsMemberAsync(userId, orgId)) return [];

        var images = await _db.Images
            .Where(i => i.OrganizationId == orgId || i.OrganizationId == null)
            .OrderByDescending(i => i.CreatedAt)
            .Take(50).ToListAsync();
        return images.Select(i => new McpOrgImageInfo(i.Id, i.Tag, i.ContentHash, i.Visibility.ToString(), i.OrganizationId, i.CreatedAt)).ToList();
    }

    [McpServerTool, Description("Check a user's role in an organization")]
    public async Task<McpOrgRoleInfo?> GetOrganizationRole(
        [Description("User ID")] string userId,
        [Description("Organization ID (GUID)")] string organizationId)
    {
        if (!Guid.TryParse(organizationId, out var orgId)) return null;
        var role = await _orgMembership.GetRoleAsync(userId, orgId);
        return role is null ? null : new McpOrgRoleInfo(userId, orgId, role);
    }
}

public record McpOrgImageInfo(Guid Id, string Tag, string ContentHash, string Visibility, Guid? OrganizationId, DateTime CreatedAt);
public record McpOrgRoleInfo(string UserId, Guid OrganizationId, string Role);
public record McpImageInfo(Guid Id, string Tag, string ContentHash, int BuildNumber, string BuildStatus, bool BuiltOffline, string Changelog, DateTime CreatedAt);
