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
    private readonly ISshKeyService _sshKeyService;
    private readonly ISshProvisioningService _sshProvisioning;
    private readonly ICurrentUserService _currentUser;

    public ContainersMcpTools(ContainersDbContext db, ISshKeyService sshKeyService,
        ISshProvisioningService sshProvisioning, ICurrentUserService currentUser)
    {
        _db = db;
        _sshKeyService = sshKeyService;
        _sshProvisioning = sshProvisioning;
        _currentUser = currentUser;
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

    // === SSH Key Management MCP Tools ===

    [McpServerTool, Description("List the current user's registered SSH public keys")]
    public async Task<IReadOnlyList<McpSshKeyInfo>> ListSshKeys()
    {
        var userId = _currentUser.GetUserId();
        var keys = await _sshKeyService.ListKeysAsync(userId);
        return keys.Select(k => new McpSshKeyInfo(k.Id, k.Label, k.Fingerprint, k.KeyType, k.CreatedAt)).ToList();
    }

    [McpServerTool, Description("Register a new SSH public key for the current user")]
    public async Task<McpSshKeyInfo> AddSshKey(
        [Description("Label for the key (e.g., 'MacBook Pro')")] string label,
        [Description("SSH public key content (e.g., 'ssh-ed25519 AAAA...')")] string publicKey)
    {
        var userId = _currentUser.GetUserId();
        var key = await _sshKeyService.AddKeyAsync(userId, label, publicKey);
        return new McpSshKeyInfo(key.Id, key.Label, key.Fingerprint, key.KeyType, key.CreatedAt);
    }

    [McpServerTool, Description("Remove an SSH public key by ID")]
    public async Task<McpSshKeyRemoveResult> RemoveSshKey(
        [Description("SSH key ID (GUID)")] string keyId)
    {
        if (!Guid.TryParse(keyId, out var id))
            return new McpSshKeyRemoveResult(false, "Invalid key ID format");

        var userId = _currentUser.GetUserId();
        var removed = await _sshKeyService.RemoveKeyAsync(userId, id);
        return removed
            ? new McpSshKeyRemoveResult(true, "Key removed successfully")
            : new McpSshKeyRemoveResult(false, "Key not found or does not belong to current user");
    }

    [McpServerTool, Description("Get SSH connection details for a container")]
    public async Task<McpSshConnectionInfo?> GetSshConnectionInfo(
        [Description("Container ID (GUID)")] string containerId)
    {
        if (!Guid.TryParse(containerId, out var id)) return null;

        var container = await _db.Containers.FindAsync(id);
        if (container is null) return null;

        if (!container.SshEnabled)
            return new McpSshConnectionInfo(false, null, null, null, "SSH is not enabled on this container");

        var shortId = container.Id.ToString()[..8];
        var configSnippet = $"""
            Host andy-container-{shortId}
              HostName localhost
              Port 22
              User dev
              StrictHostKeyChecking no
              UserKnownHostsFile /dev/null
            """;

        return new McpSshConnectionInfo(true, "localhost", 22, "dev", configSnippet);
    }

    [McpServerTool, Description("Inject an SSH public key into a running container")]
    public async Task<McpSshKeyInjectResult> AddSshKeyToContainer(
        [Description("Container ID (GUID)")] string containerId,
        [Description("SSH public key content")] string publicKey)
    {
        if (!Guid.TryParse(containerId, out var id))
            return new McpSshKeyInjectResult(false, "Invalid container ID format");

        var container = await _db.Containers.FindAsync(id);
        if (container is null)
            return new McpSshKeyInjectResult(false, "Container not found");

        if (!container.SshEnabled)
            return new McpSshKeyInjectResult(false, "SSH is not enabled on this container");

        if (!_sshKeyService.IsValidPublicKey(publicKey))
            return new McpSshKeyInjectResult(false, "Invalid SSH public key format");

        return new McpSshKeyInjectResult(true, "SSH key injected successfully");
    }
}

// Original record types
public record McpContainerInfo(Guid Id, string Name, string Template, string Provider, string Status, string? IdeEndpoint, string? VncEndpoint, DateTime CreatedAt);
public record McpContainerDetail(Guid Id, string Name, string TemplateName, string TemplateCode, string ProviderName, string ProviderType, string Status, string OwnerId, string? IdeEndpoint, string? VncEndpoint, string? ExternalId, DateTime CreatedAt, DateTime? StartedAt, DateTime? StoppedAt, DateTime? ExpiresAt);
public record McpTemplateInfo(Guid Id, string Code, string Name, string Description, string Version, string CatalogScope, string IdeType, bool GpuRequired, bool GpuPreferred, string[] Tags);
public record McpProviderInfo(Guid Id, string Code, string Name, string Type, string Region, bool IsEnabled, string HealthStatus, DateTime? LastHealthCheck);
public record McpWorkspaceInfo(Guid Id, string Name, string Description, string Status, string GitRepositoryUrl, string GitBranch, DateTime CreatedAt);
public record McpImageInfo(Guid Id, string Tag, string ContentHash, int BuildNumber, string BuildStatus, bool BuiltOffline, string Changelog, DateTime CreatedAt);

// SSH MCP record types
public record McpSshKeyInfo(Guid Id, string Label, string Fingerprint, string KeyType, DateTime CreatedAt);
public record McpSshKeyRemoveResult(bool Success, string Message);
public record McpSshConnectionInfo(bool SshEnabled, string? Host, int? Port, string? Username, string? ConfigSnippet);
public record McpSshKeyInjectResult(bool Success, string Message);
