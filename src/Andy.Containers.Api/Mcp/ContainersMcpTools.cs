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
    private readonly ITemplateValidator _templateValidator;

    public ContainersMcpTools(ContainersDbContext db, ITemplateValidator templateValidator)
    {
        _db = db;
        _templateValidator = templateValidator;
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

    [McpServerTool, Description("Validate a template YAML definition and return errors/warnings")]
    public async Task<McpValidationResult> ValidateTemplateYaml(
        [Description("YAML content to validate")] string yaml)
    {
        var result = await _templateValidator.ValidateYamlAsync(yaml);
        return new McpValidationResult(
            result.IsValid,
            result.Errors.Select(e => $"[{e.Field}] {e.Message}").ToArray(),
            result.Warnings.Select(w => $"[{w.Field}] {w.Message}").ToArray());
    }

    [McpServerTool, Description("Get the YAML definition for a template by code")]
    public async Task<McpTemplateDefinition?> GetTemplateDefinition(
        [Description("Template code (e.g., full-stack)")] string templateCode)
    {
        var template = await _db.Templates.FirstOrDefaultAsync(t => t.Code == templateCode);
        if (template is null) return null;
        var yaml = $"code: {template.Code}\nname: {template.Name}\nversion: {template.Version}\nbase_image: {template.BaseImage}\nide_type: {template.IdeType}\nscope: {template.CatalogScope}";
        return new McpTemplateDefinition(template.Code, template.Name, yaml);
    }

    [McpServerTool, Description("Create a new template from a YAML definition")]
    public async Task<McpCreateFromYamlResult> CreateTemplateFromYaml(
        [Description("YAML content for the new template")] string yaml)
    {
        var validation = await _templateValidator.ValidateYamlAsync(yaml);
        if (!validation.IsValid)
            return new McpCreateFromYamlResult(false, null, null, validation.Errors.Select(e => e.Message).ToArray());

        var template = await _templateValidator.ParseYamlToTemplateAsync(yaml);
        if (template is null)
            return new McpCreateFromYamlResult(false, null, null, ["Failed to parse YAML"]);

        _db.Templates.Add(template);
        await _db.SaveChangesAsync();
        return new McpCreateFromYamlResult(true, template.Id, template.Code, []);
    }

    [McpServerTool, Description("Update a template's definition from YAML")]
    public async Task<McpUpdateDefinitionResult> UpdateTemplateDefinition(
        [Description("Template ID (GUID)")] string templateId,
        [Description("YAML content for the template")] string yaml)
    {
        if (!Guid.TryParse(templateId, out var id))
            return new McpUpdateDefinitionResult(false, ["Invalid template ID"]);

        var template = await _db.Templates.FindAsync(id);
        if (template is null)
            return new McpUpdateDefinitionResult(false, ["Template not found"]);

        var validation = await _templateValidator.ValidateYamlAsync(yaml);
        if (!validation.IsValid)
            return new McpUpdateDefinitionResult(false, validation.Errors.Select(e => $"[{e.Field}] {e.Message}").ToArray());

        var parsed = await _templateValidator.ParseYamlToTemplateAsync(yaml);
        if (parsed is null)
            return new McpUpdateDefinitionResult(false, ["Failed to parse YAML"]);

        template.Name = parsed.Name;
        template.Description = parsed.Description;
        template.Version = parsed.Version;
        template.BaseImage = parsed.BaseImage;
        template.IdeType = parsed.IdeType;
        template.GpuRequired = parsed.GpuRequired;
        template.GpuPreferred = parsed.GpuPreferred;
        template.Tags = parsed.Tags;
        template.DefaultResources = parsed.DefaultResources;
        template.EnvironmentVariables = parsed.EnvironmentVariables;
        template.Ports = parsed.Ports;
        template.Scripts = parsed.Scripts;
        template.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return new McpUpdateDefinitionResult(true, []);
    }
}

public record McpContainerInfo(Guid Id, string Name, string Template, string Provider, string Status, string? IdeEndpoint, string? VncEndpoint, DateTime CreatedAt);
public record McpContainerDetail(Guid Id, string Name, string TemplateName, string TemplateCode, string ProviderName, string ProviderType, string Status, string OwnerId, string? IdeEndpoint, string? VncEndpoint, string? ExternalId, DateTime CreatedAt, DateTime? StartedAt, DateTime? StoppedAt, DateTime? ExpiresAt);
public record McpTemplateInfo(Guid Id, string Code, string Name, string Description, string Version, string CatalogScope, string IdeType, bool GpuRequired, bool GpuPreferred, string[] Tags);
public record McpProviderInfo(Guid Id, string Code, string Name, string Type, string Region, bool IsEnabled, string HealthStatus, DateTime? LastHealthCheck);
public record McpWorkspaceInfo(Guid Id, string Name, string Description, string Status, string GitRepositoryUrl, string GitBranch, DateTime CreatedAt);
public record McpValidationResult(bool IsValid, string[] Errors, string[] Warnings);
public record McpTemplateDefinition(string Code, string Name, string Yaml);
public record McpCreateFromYamlResult(bool Success, Guid? TemplateId, string? Code, string[] Errors);
public record McpUpdateDefinitionResult(bool Success, string[] Errors);
public record McpImageInfo(Guid Id, string Tag, string ContentHash, int BuildNumber, string BuildStatus, bool BuiltOffline, string Changelog, DateTime CreatedAt);
