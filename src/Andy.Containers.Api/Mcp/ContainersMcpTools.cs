using System.ComponentModel;
using Andy.Containers.Abstractions;
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
    private readonly IContainerService _containerService;
    private readonly IGitCloneService _gitCloneService;
    private readonly IGitCredentialService _credentialService;
    private readonly IGitRepositoryProbeService _probeService;
    private readonly IImageManifestService _manifestService;
    private readonly IImageDiffService _diffService;
    private readonly ICurrentUserService _currentUser;
    private readonly IOrganizationMembershipService _orgMembership;
    private readonly IApiKeyService _apiKeyService;

    public ContainersMcpTools(
        ContainersDbContext db,
        IContainerService containerService,
        IGitCloneService gitCloneService,
        IGitCredentialService credentialService,
        IGitRepositoryProbeService probeService,
        IImageManifestService manifestService,
        IImageDiffService diffService,
        ICurrentUserService currentUser,
        IOrganizationMembershipService orgMembership,
        IApiKeyService apiKeyService)
    {
        _db = db;
        _containerService = containerService;
        _gitCloneService = gitCloneService;
        _credentialService = credentialService;
        _probeService = probeService;
        _manifestService = manifestService;
        _diffService = diffService;
        _currentUser = currentUser;
        _orgMembership = orgMembership;
        _apiKeyService = apiKeyService;
    }

    [McpServerTool, Description("List all containers with their status")]
    public async Task<IReadOnlyList<McpContainerInfo>> ListContainers(
        [Description("Filter by status: Pending, Creating, Running, Stopped, Failed, Destroyed")] string? status = null,
        [Description("Filter by organization ID (GUID)")] string? organizationId = null)
    {
        ContainerStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<ContainerStatus>(status, true, out var s))
            statusFilter = s;

        Guid? orgId = null;
        if (!string.IsNullOrEmpty(organizationId) && Guid.TryParse(organizationId, out var parsedOrgId))
        {
            if (!_currentUser.IsAdmin())
            {
                var isMember = await _orgMembership.IsMemberAsync(_currentUser.GetUserId(), parsedOrgId);
                if (!isMember) return [];
            }
            orgId = parsedOrgId;
        }

        var containers = await _containerService.ListContainersAsync(new ContainerFilter
        {
            Status = statusFilter,
            OrganizationId = orgId,
            Take = 50
        });
        return containers.Select(c => new McpContainerInfo(c.Id, c.Name, c.Template?.Name ?? "", c.Provider?.Name ?? "", c.Status.ToString(), c.IdeEndpoint, c.VncEndpoint, c.CreatedAt)).ToList();
    }

    [McpServerTool, Description("Get detailed info about a specific container")]
    public async Task<McpContainerDetail?> GetContainer([Description("Container ID (GUID)")] string containerId)
    {
        if (!Guid.TryParse(containerId, out var id)) return null;
        try
        {
            var c = await _containerService.GetContainerAsync(id);
            return new McpContainerDetail(c.Id, c.Name, c.Template?.Name ?? "", c.Template?.Code ?? "", c.Provider?.Name ?? "", c.Provider?.Type.ToString() ?? "", c.Status.ToString(), c.OwnerId, c.IdeEndpoint, c.VncEndpoint, c.ExternalId, c.CreatedAt, c.StartedAt, c.StoppedAt, c.ExpiresAt);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    [McpServerTool, Description("Browse the container template catalog")]
    public async Task<IReadOnlyList<McpTemplateInfo>> BrowseTemplates(
        [Description("Search by name or description")] string? search = null,
        [Description("Filter by scope: Global, Organization, Team, User")] string? scope = null,
        [Description("Filter by organization ID (GUID)")] string? organizationId = null)
    {
        var query = _db.Templates.Where(t => t.IsPublished).AsQueryable();
        if (!string.IsNullOrEmpty(search))
            query = query.Where(t => t.Name.Contains(search) || (t.Description != null && t.Description.Contains(search)));
        if (!string.IsNullOrEmpty(scope) && Enum.TryParse<CatalogScope>(scope, true, out var s))
            query = query.Where(t => t.CatalogScope == s);
        if (!string.IsNullOrEmpty(organizationId) && Guid.TryParse(organizationId, out var orgId))
        {
            if (!_currentUser.IsAdmin())
            {
                var isMember = await _orgMembership.IsMemberAsync(_currentUser.GetUserId(), orgId);
                if (!isMember) return [];
            }
            query = query.Where(t => t.OrganizationId == orgId || t.CatalogScope == CatalogScope.Global);
        }
        var templates = await query.OrderBy(t => t.Name).Take(50).ToListAsync();
        return templates.Select(t => new McpTemplateInfo(t.Id, t.Code, t.Name, t.Description ?? "", t.Version, t.CatalogScope.ToString(), t.IdeType.ToString(), t.GpuRequired, t.GpuPreferred, t.Tags ?? [])).ToList();
    }

    [McpServerTool, Description("List infrastructure providers and their health status")]
    public async Task<IReadOnlyList<McpProviderInfo>> ListProviders(
        [Description("Filter by organization ID (GUID)")] string? organizationId = null)
    {
        var query = _db.Providers.AsQueryable();
        if (!string.IsNullOrEmpty(organizationId) && Guid.TryParse(organizationId, out var orgId))
        {
            if (!_currentUser.IsAdmin())
            {
                var isMember = await _orgMembership.IsMemberAsync(_currentUser.GetUserId(), orgId);
                if (!isMember) return [];
            }
            query = query.Where(p => p.OrganizationId == null || p.OrganizationId == orgId);
        }
        var providers = await query.OrderBy(p => p.Name).ToListAsync();
        return providers.Select(p => new McpProviderInfo(p.Id, p.Code, p.Name, p.Type.ToString(), p.Region ?? "", p.IsEnabled, p.HealthStatus.ToString(), p.LastHealthCheck)).ToList();
    }

    [McpServerTool, Description("List workspaces")]
    public async Task<IReadOnlyList<McpWorkspaceInfo>> ListWorkspaces(
        [Description("Filter by organization ID (GUID)")] string? organizationId = null)
    {
        var query = _db.Workspaces.AsQueryable();
        if (!string.IsNullOrEmpty(organizationId) && Guid.TryParse(organizationId, out var orgId))
        {
            if (!_currentUser.IsAdmin())
            {
                var isMember = await _orgMembership.IsMemberAsync(_currentUser.GetUserId(), orgId);
                if (!isMember) return [];
            }
            query = query.Where(w => w.OrganizationId == orgId);
        }
        var workspaces = await query.OrderByDescending(w => w.CreatedAt).Take(50).ToListAsync();
        return workspaces.Select(w => new McpWorkspaceInfo(w.Id, w.Name, w.Description ?? "", w.Status.ToString(), w.GitRepositoryUrl ?? "", w.GitBranch ?? "", w.CreatedAt)).ToList();
    }

    [McpServerTool, Description("List built images for a template")]
    public async Task<IReadOnlyList<McpImageInfo>> ListImages(
        [Description("Template code (e.g., full-stack)")] string templateCode,
        [Description("Filter by organization ID (GUID)")] string? organizationId = null)
    {
        var template = await _db.Templates.FirstOrDefaultAsync(t => t.Code == templateCode);
        if (template is null) return [];
        var query = _db.Images.Where(i => i.TemplateId == template.Id);
        if (!string.IsNullOrEmpty(organizationId) && Guid.TryParse(organizationId, out var orgId))
        {
            if (!_currentUser.IsAdmin())
            {
                var isMember = await _orgMembership.IsMemberAsync(_currentUser.GetUserId(), orgId);
                if (!isMember) return [];
            }
            query = query.Where(i => i.OrganizationId == null || i.OrganizationId == orgId);
        }
        var images = await query.OrderByDescending(i => i.BuildNumber).Take(20).ToListAsync();
        return images.Select(i => new McpImageInfo(i.Id, i.Tag, i.ContentHash, i.BuildNumber, i.BuildStatus.ToString(), i.BuiltOffline, i.Changelog ?? "", i.CreatedAt)).ToList();
    }

    [McpServerTool, Description("List git repositories cloned into a container with their clone status")]
    public async Task<IReadOnlyList<McpGitRepositoryInfo>> ListContainerRepositories(
        [Description("Container ID (GUID)")] string containerId)
    {
        if (!Guid.TryParse(containerId, out var id)) return [];
        var repos = await _db.ContainerGitRepositories
            .Where(r => r.ContainerId == id)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();
        return repos.Select(r => new McpGitRepositoryInfo(r.Id, r.Url, r.Branch ?? "", r.TargetPath, r.CloneStatus.ToString(), r.CloneError ?? "", r.IsFromTemplate, r.CloneStartedAt, r.CloneCompletedAt)).ToList();
    }

    [McpServerTool, Description("Clone a new git repository into a running container")]
    public async Task<McpGitRepositoryInfo?> CloneRepository(
        [Description("Container ID (GUID)")] string containerId,
        [Description("Git repository URL (https:// or git@)")] string url,
        [Description("Branch to clone")] string? branch = null,
        [Description("Target path inside the container")] string? targetPath = null,
        [Description("Credential label for private repos")] string? credentialRef = null,
        [Description("Shallow clone depth")] int? cloneDepth = null,
        [Description("Include submodules")] bool submodules = false)
    {
        if (!Guid.TryParse(containerId, out var id)) return null;

        var config = new GitRepositoryConfig { Url = url, Branch = branch, TargetPath = targetPath, CredentialRef = credentialRef, CloneDepth = cloneDepth, Submodules = submodules };
        var errors = GitRepositoryValidator.Validate(config);
        if (errors.Count > 0) return null;

        // Validate credentials — get container owner for credential resolution
        var container = await _db.Containers.FindAsync(id);
        if (container is null) return null;

        var probeErrors = await _probeService.ProbeRepositoriesAsync(
            [config], container.OwnerId, requireCredentials: true);
        if (probeErrors.Count > 0) return null; // MCP tools return null for errors

        var repo = new ContainerGitRepository
        {
            ContainerId = id,
            Url = url,
            Branch = branch,
            TargetPath = targetPath ?? "/workspace",
            CredentialRef = credentialRef,
            CloneDepth = cloneDepth,
            Submodules = submodules,
            CloneStatus = GitCloneStatus.Pending
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        var cloned = await _gitCloneService.CloneRepositoryAsync(id, repo.Id);
        return new McpGitRepositoryInfo(cloned.Id, cloned.Url, cloned.Branch ?? "", cloned.TargetPath, cloned.CloneStatus.ToString(), cloned.CloneError ?? "", cloned.IsFromTemplate, cloned.CloneStartedAt, cloned.CloneCompletedAt);
    }

    [McpServerTool, Description("Pull latest changes for a cloned repository")]
    public async Task<McpGitRepositoryInfo?> PullRepository(
        [Description("Container ID (GUID)")] string containerId,
        [Description("Repository ID (GUID)")] string repositoryId)
    {
        if (!Guid.TryParse(containerId, out var cId) || !Guid.TryParse(repositoryId, out var rId)) return null;
        var repo = await _gitCloneService.PullRepositoryAsync(cId, rId);
        return new McpGitRepositoryInfo(repo.Id, repo.Url, repo.Branch ?? "", repo.TargetPath, repo.CloneStatus.ToString(), repo.CloneError ?? "", repo.IsFromTemplate, repo.CloneStartedAt, repo.CloneCompletedAt);
    }

    [McpServerTool, Description("List stored git credentials (tokens are never returned)")]
    public async Task<IReadOnlyList<McpGitCredentialInfo>> ListGitCredentials(
        [Description("Owner ID to list credentials for")] string ownerId)
    {
        var credentials = await _credentialService.ListAsync(ownerId);
        return credentials.Select(c => new McpGitCredentialInfo(c.Id, c.Label, c.GitHost ?? "", c.CredentialType.ToString(), c.CreatedAt, c.LastUsedAt)).ToList();
    }

    [McpServerTool, Description("Store a git credential (PAT or deploy key) for cloning private repositories")]
    public async Task<McpGitCredentialInfo?> StoreGitCredential(
        [Description("Owner ID")] string ownerId,
        [Description("Label for this credential (e.g., 'github-work')")] string label,
        [Description("The token or key value")] string token,
        [Description("Git host to auto-match (e.g., github.com)")] string? gitHost = null)
    {
        var credential = await _credentialService.CreateAsync(ownerId, label, token, gitHost);
        return new McpGitCredentialInfo(credential.Id, credential.Label, credential.GitHost ?? "", credential.CredentialType.ToString(), credential.CreatedAt, credential.LastUsedAt);
    }

    [McpServerTool, Description("Get the introspection manifest for a built image, showing installed tools, OS packages, and base image info")]
    public async Task<McpImageManifestInfo?> GetImageManifest(
        [Description("Image ID (GUID)")] string imageId)
    {
        if (!Guid.TryParse(imageId, out var id)) return null;
        var manifest = await _manifestService.GetManifestAsync(id);
        if (manifest is null) return null;
        return new McpImageManifestInfo(
            manifest.ImageContentHash,
            manifest.BaseImage,
            manifest.BaseImageDigest,
            manifest.Architecture,
            $"{manifest.OperatingSystem.Name} {manifest.OperatingSystem.Version}",
            manifest.Tools.Count,
            manifest.OsPackages.Count,
            manifest.IntrospectedAt);
    }

    [McpServerTool, Description("List the developer tools installed in a built image with their versions and types")]
    public async Task<IReadOnlyList<McpInstalledToolInfo>> GetImageTools(
        [Description("Image ID (GUID)")] string imageId)
    {
        if (!Guid.TryParse(imageId, out var id)) return [];
        var manifest = await _manifestService.GetManifestAsync(id);
        if (manifest is null) return [];
        return manifest.Tools.Select(t => new McpInstalledToolInfo(t.Name, t.Version, t.Type.ToString(), t.MatchesDeclared)).ToList();
    }

    [McpServerTool, Description("Compare two images to see what tools, packages, and configurations changed between them")]
    public async Task<McpImageDiffInfo?> CompareImages(
        [Description("From image ID (GUID)")] string fromImageId,
        [Description("To image ID (GUID)")] string toImageId)
    {
        if (!Guid.TryParse(fromImageId, out var fromId) || !Guid.TryParse(toImageId, out var toId)) return null;
        try
        {
            var diff = await _diffService.DiffAsync(fromId, toId);
            return new McpImageDiffInfo(
                diff.BaseImageChanged,
                diff.OsVersionChanged,
                diff.ArchitectureChanged,
                diff.ToolChanges.Select(c => new McpToolChange(c.Name, c.ChangeType, c.PreviousVersion, c.NewVersion, c.Severity)).ToList(),
                diff.PackageChanges.Added,
                diff.PackageChanges.Removed,
                diff.PackageChanges.Upgraded,
                diff.PackageChanges.Downgraded,
                diff.SizeChange,
                diff.Warning);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    [McpServerTool, Description("Find images that have a specific tool installed")]
    public async Task<IReadOnlyList<McpImageInfo>> FindImageByTool(
        [Description("Tool name to search for (e.g., python, node, dotnet-sdk)")] string toolName,
        [Description("Template code to limit search")] string? templateCode = null)
    {
        var query = _db.Images.AsQueryable();
        if (!string.IsNullOrEmpty(templateCode))
        {
            var template = await _db.Templates.FirstOrDefaultAsync(t => t.Code == templateCode);
            if (template is null) return [];
            query = query.Where(i => i.TemplateId == template.Id);
        }
        var images = await query
            .Where(i => i.DependencyManifest != null && i.DependencyManifest.Contains(toolName))
            .OrderByDescending(i => i.BuildNumber)
            .Take(20)
            .ToListAsync();
        return images.Select(i => new McpImageInfo(i.Id, i.Tag, i.ContentHash, i.BuildNumber, i.BuildStatus.ToString(), i.BuiltOffline, i.Changelog ?? "", i.CreatedAt)).ToList();
    }

    [McpServerTool, Description("Get a summary of an organization's resources including templates, images, containers, and providers")]
    public async Task<McpOrgResourceSummary?> GetOrganizationResources(
        [Description("Organization ID (GUID)")] string organizationId)
    {
        if (!Guid.TryParse(organizationId, out var orgId)) return null;

        var userId = _currentUser.GetUserId();
        if (!_currentUser.IsAdmin())
        {
            var isMember = await _orgMembership.IsMemberAsync(userId, orgId);
            if (!isMember) return null;
        }

        var templateCount = await _db.Templates.CountAsync(t => t.OrganizationId == orgId);
        var imageCount = await _db.Images.CountAsync(i => i.OrganizationId == orgId);
        var containerCount = await _db.Containers.CountAsync(c => c.OrganizationId == orgId);
        var providerCount = await _db.Providers.CountAsync(p => p.OrganizationId == orgId);

        return new McpOrgResourceSummary(orgId, templateCount, imageCount, containerCount, providerCount);
    }

    [McpServerTool, Description("Trigger an organization-scoped image build from a template")]
    public async Task<McpImageInfo?> BuildOrganizationImage(
        [Description("Organization ID (GUID)")] string organizationId,
        [Description("Template code (e.g., full-stack)")] string templateCode)
    {
        if (!Guid.TryParse(organizationId, out var orgId)) return null;

        var userId = _currentUser.GetUserId();
        if (!_currentUser.IsAdmin())
        {
            var hasPermission = await _orgMembership.HasPermissionAsync(userId, orgId, Permissions.ImageBuild);
            if (!hasPermission) return null;
        }

        var template = await _db.Templates.FirstOrDefaultAsync(t => t.Code == templateCode);
        if (template is null) return null;

        var image = new ContainerImage
        {
            TemplateId = template.Id,
            ContentHash = $"sha256:{Guid.NewGuid():N}",
            Tag = $"{template.Code}:{template.Version}-{DateTime.UtcNow:yyyyMMddHHmmss}",
            ImageReference = $"andy-containers/{template.Code}:{template.Version}",
            BaseImageDigest = $"sha256:{Guid.NewGuid():N}",
            DependencyManifest = "{}",
            DependencyLock = "{}",
            BuildNumber = await _db.Images.CountAsync(i => i.TemplateId == template.Id) + 1,
            BuildStatus = ImageBuildStatus.Building,
            BuildStartedAt = DateTime.UtcNow,
            OrganizationId = orgId,
            OwnerId = userId,
            Visibility = ImageVisibility.Organization
        };
        _db.Images.Add(image);
        await _db.SaveChangesAsync();

        try
        {
            var (manifest, finalImage) = await _manifestService.GenerateManifestAsync(image.Id);
            finalImage.BuildStatus = ImageBuildStatus.Succeeded;
            finalImage.BuildCompletedAt = DateTime.UtcNow;
            finalImage.Changelog = "Organization-scoped build";
            await _db.SaveChangesAsync();
            image = finalImage;
        }
        catch
        {
            image.BuildStatus = ImageBuildStatus.Succeeded;
            image.BuildCompletedAt = DateTime.UtcNow;
            image.Changelog = "Organization-scoped build (introspection unavailable)";
            await _db.SaveChangesAsync();
        }

        return new McpImageInfo(image.Id, image.Tag, image.ContentHash, image.BuildNumber, image.BuildStatus.ToString(), image.BuiltOffline, image.Changelog ?? "", image.CreatedAt);
    }
    [McpServerTool, Description("Create a new development container")]
    public async Task<string> CreateContainer(
        [Description("Container name")] string name,
        [Description("Template code (e.g., dotnet-8-alpine, full-stack)")] string templateCode,
        [Description("Provider code (optional, auto-selects if omitted)")] string? providerCode = null,
        [Description("Code assistant tool (ClaudeCode, Aider, OpenCode, CodexCli, etc.)")] string? codeAssistant = null,
        [Description("LLM model name (e.g., gpt-4o, claude-sonnet-4-20250514)")] string? model = null,
        [Description("API base URL (e.g., https://openrouter.ai/api/v1)")] string? baseUrl = null)
    {
        var userId = _currentUser.GetUserId();
        var request = new CreateContainerRequest
        {
            Name = name,
            TemplateCode = templateCode,
            ProviderCode = providerCode,
            OwnerId = userId,
            Source = CreationSource.Mcp,
        };

        if (!string.IsNullOrEmpty(codeAssistant))
        {
            if (Enum.TryParse<CodeAssistantType>(codeAssistant, true, out var toolType))
            {
                request.CodeAssistant = new CodeAssistantConfig
                {
                    Tool = toolType,
                    ModelName = model,
                    ApiBaseUrl = baseUrl,
                };
            }
        }

        var container = await _containerService.CreateContainerAsync(request, CancellationToken.None);
        return $"Container '{container.Name}' created (ID: {container.Id}, Status: {container.Status})";
    }

    [McpServerTool, Description("Store an API key for an AI code assistant provider. The key is validated immediately and encrypted at rest.")]
    public async Task<McpApiKeyInfo?> StoreApiKey(
        [Description("Provider: Anthropic, OpenAI, Google, Dashscope, Custom")] string provider,
        [Description("Label for this key (e.g., 'my-anthropic-key')")] string label,
        [Description("The API key value")] string apiKey,
        [Description("Environment variable name (defaults based on provider)")] string? envVarName = null)
    {
        if (!Enum.TryParse<ApiKeyProvider>(provider, true, out var p)) return null;
        var userId = _currentUser.GetUserId();
        var credential = await _apiKeyService.CreateAsync(userId, label, p, apiKey, envVarName);
        return new McpApiKeyInfo(credential.Id, credential.Label, credential.Provider.ToString(), credential.EnvVarName, credential.MaskedValue ?? "****", credential.IsValid, credential.LastValidatedAt, credential.LastUsedAt, credential.CreatedAt);
    }

    [McpServerTool, Description("List stored API keys for the current user (values are never returned)")]
    public async Task<IReadOnlyList<McpApiKeyInfo>> ListApiKeys()
    {
        var userId = _currentUser.GetUserId();
        var keys = await _apiKeyService.ListAsync(userId);
        return keys.Select(k => new McpApiKeyInfo(k.Id, k.Label, k.Provider.ToString(), k.EnvVarName, k.MaskedValue ?? "****", k.IsValid, k.LastValidatedAt, k.LastUsedAt, k.CreatedAt)).ToList();
    }

    [McpServerTool, Description("Delete a stored API key")]
    public async Task<bool> DeleteApiKey(
        [Description("API key ID (GUID)")] string apiKeyId)
    {
        if (!Guid.TryParse(apiKeyId, out var id)) return false;
        var userId = _currentUser.GetUserId();
        return await _apiKeyService.DeleteAsync(id, userId);
    }

    [McpServerTool, Description("Re-validate a stored API key against the provider's API")]
    public async Task<McpApiKeyValidationInfo?> ValidateApiKey(
        [Description("API key ID (GUID)")] string apiKeyId)
    {
        if (!Guid.TryParse(apiKeyId, out var id)) return null;
        var userId = _currentUser.GetUserId();
        var result = await _apiKeyService.ValidateExistingAsync(id, userId);
        return new McpApiKeyValidationInfo(result.IsValid, result.Error);
    }
}

public record McpApiKeyInfo(Guid Id, string Label, string Provider, string EnvVarName, string MaskedValue, bool IsValid, DateTime? LastValidatedAt, DateTime? LastUsedAt, DateTime CreatedAt);
public record McpApiKeyValidationInfo(bool IsValid, string? Error);
public record McpGitRepositoryInfo(Guid Id, string Url, string Branch, string TargetPath, string CloneStatus, string CloneError, bool IsFromTemplate, DateTime? CloneStartedAt, DateTime? CloneCompletedAt);
public record McpGitCredentialInfo(Guid Id, string Label, string GitHost, string CredentialType, DateTime CreatedAt, DateTime? LastUsedAt);
public record McpContainerInfo(Guid Id, string Name, string Template, string Provider, string Status, string? IdeEndpoint, string? VncEndpoint, DateTime CreatedAt);
public record McpContainerDetail(Guid Id, string Name, string TemplateName, string TemplateCode, string ProviderName, string ProviderType, string Status, string OwnerId, string? IdeEndpoint, string? VncEndpoint, string? ExternalId, DateTime CreatedAt, DateTime? StartedAt, DateTime? StoppedAt, DateTime? ExpiresAt);
public record McpTemplateInfo(Guid Id, string Code, string Name, string Description, string Version, string CatalogScope, string IdeType, bool GpuRequired, bool GpuPreferred, string[] Tags);
public record McpProviderInfo(Guid Id, string Code, string Name, string Type, string Region, bool IsEnabled, string HealthStatus, DateTime? LastHealthCheck);
public record McpWorkspaceInfo(Guid Id, string Name, string Description, string Status, string GitRepositoryUrl, string GitBranch, DateTime CreatedAt);
public record McpImageInfo(Guid Id, string Tag, string ContentHash, int BuildNumber, string BuildStatus, bool BuiltOffline, string Changelog, DateTime CreatedAt);
public record McpImageManifestInfo(string ContentHash, string BaseImage, string BaseImageDigest, string Architecture, string OperatingSystem, int ToolCount, int PackageCount, DateTime IntrospectedAt);
public record McpInstalledToolInfo(string Name, string Version, string Type, bool MatchesDeclared);
public record McpImageDiffInfo(bool BaseImageChanged, string? OsVersionChanged, bool ArchitectureChanged, List<McpToolChange> ToolChanges, int PackagesAdded, int PackagesRemoved, int PackagesUpgraded, int PackagesDowngraded, string? SizeChange, string? Warning);
public record McpToolChange(string Name, string ChangeType, string? PreviousVersion, string? NewVersion, string? Severity);
public record McpOrgResourceSummary(Guid OrganizationId, int TemplateCount, int ImageCount, int ContainerCount, int ProviderCount);
