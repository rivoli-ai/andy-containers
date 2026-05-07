using System.Text.Json;
using Andy.Containers.Api.Services;
using Andy.Containers.Crypto;
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
public class TemplatesController : ControllerBase
{
    private readonly ContainersDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly ICurrentUserService _currentUser;
    private readonly IYamlTemplateParser _parser;
    private readonly IOrganizationMembershipService _orgMembership;
    private readonly ITemplateBuildService _buildService;

    public TemplatesController(ContainersDbContext db, IWebHostEnvironment env, ICurrentUserService currentUser, IYamlTemplateParser parser, IOrganizationMembershipService orgMembership, ITemplateBuildService buildService)
    {
        _db = db;
        _env = env;
        _currentUser = currentUser;
        _parser = parser;
        _orgMembership = orgMembership;
        _buildService = buildService;
    }

    [RequirePermission("template:read")]
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] CatalogScope? scope,
        [FromQuery] Guid? organizationId,
        [FromQuery] Guid? teamId,
        [FromQuery] string? search,
        [FromQuery] bool? gpuRequired,
        [FromQuery] IdeType? ideType,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken ct = default)
    {
        var query = _db.Templates.AsQueryable();

        if (organizationId.HasValue && !_currentUser.IsAdmin())
        {
            var isMember = await _orgMembership.IsMemberAsync(_currentUser.GetUserId(), organizationId.Value, ct);
            if (!isMember) return Forbid();
        }

        if (scope.HasValue)
            query = query.Where(t => t.CatalogScope == scope);
        if (organizationId.HasValue)
            query = query.Where(t => t.OrganizationId == organizationId || t.CatalogScope == CatalogScope.Global);
        if (teamId.HasValue)
            query = query.Where(t => t.TeamId == teamId || t.CatalogScope <= CatalogScope.Organization);
        if (!string.IsNullOrEmpty(search))
            query = query.Where(t =>
                t.Name.Contains(search)
                || (t.Description != null && t.Description.Contains(search))
                || t.Code.Contains(search)
                || (t.Tags != null && t.Tags.Contains(search)));
        if (gpuRequired.HasValue)
            query = query.Where(t => t.GpuRequired == gpuRequired);
        if (ideType.HasValue)
            query = query.Where(t => t.IdeType == ideType);

        query = query.Where(t => t.IsPublished).OrderBy(t => t.Name);
        var total = await query.CountAsync(ct);
        var items = await query.Skip(skip).Take(take).ToListAsync(ct);
        return Ok(new { items, totalCount = total });
    }

    [RequirePermission("template:read")]
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var template = await _db.Templates.FindAsync([id], ct);
        return template is null ? NotFound() : Ok(template);
    }

    [RequirePermission("template:read")]
    [HttpGet("by-code/{code}")]
    public async Task<IActionResult> GetByCode(string code, CancellationToken ct)
    {
        var template = await _db.Templates.FirstOrDefaultAsync(t => t.Code == code, ct);
        return template is null ? NotFound() : Ok(template);
    }

    [RequirePermission("template:read")]
    [HttpGet("{id:guid}/definition")]
    public async Task<IActionResult> GetDefinition(Guid id, CancellationToken ct)
    {
        var template = await _db.Templates.FindAsync([id], ct);
        if (template is null) return NotFound();

        // Search for the YAML file in config/templates directories
        // Try multiple possible root locations to be resilient to different working directories
        string[] candidates = [];
        foreach (var root in GetConfigSearchPaths())
        {
            if (Directory.Exists(root))
            {
                candidates = Directory.GetFiles(root, $"{template.Code}.yaml", SearchOption.AllDirectories);
                if (candidates.Length > 0) break;
            }
        }

        if (candidates.Length > 0)
        {
            var yaml = await System.IO.File.ReadAllTextAsync(candidates[0], ct);
            return Ok(new { code = template.Code, content = yaml });
        }

        // No YAML file on disk — generate a synthetic definition from DB fields
        var syntheticYaml = GenerateSyntheticYaml(template);
        return Ok(new { code = template.Code, content = syntheticYaml });
    }

    private IEnumerable<string> GetConfigSearchPaths()
    {
        // From ContentRootPath (project dir when using dotnet run)
        yield return Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "..", "config", "templates"));
        // From ContentRootPath (if run from repo root)
        yield return Path.Combine(_env.ContentRootPath, "config", "templates");
        // Walk up from ContentRootPath to find config/templates
        var dir = _env.ContentRootPath;
        for (var i = 0; i < 5; i++)
        {
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null) break;
            var candidate = Path.Combine(parent, "config", "templates");
            if (Directory.Exists(candidate))
            {
                yield return candidate;
                break;
            }
            dir = parent;
        }
    }

    private static string GenerateSyntheticYaml(ContainerTemplate template)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"code: {template.Code}");
        sb.AppendLine($"name: {template.Name}");
        if (!string.IsNullOrEmpty(template.Description))
            sb.AppendLine($"description: \"{template.Description}\"");
        sb.AppendLine($"version: {template.Version}");
        sb.AppendLine($"base_image: {template.BaseImage}");
        sb.AppendLine($"ide_type: {template.IdeType}");
        sb.AppendLine($"scope: {template.CatalogScope}");
        if (template.GpuRequired) sb.AppendLine("gpu_required: true");
        if (template.GpuPreferred) sb.AppendLine("gpu_preferred: true");
        if (!string.IsNullOrEmpty(template.DefaultResources))
            sb.AppendLine($"resources: {template.DefaultResources}");
        if (!string.IsNullOrEmpty(template.Ports))
            sb.AppendLine($"ports: {template.Ports}");
        if (!string.IsNullOrEmpty(template.EnvironmentVariables))
            sb.AppendLine($"environment: {template.EnvironmentVariables}");
        if (!string.IsNullOrEmpty(template.Scripts))
            sb.AppendLine($"scripts: {template.Scripts}");
        if (template.Tags is { Length: > 0 })
            sb.AppendLine($"tags: [{string.Join(", ", template.Tags)}]");
        return sb.ToString();
    }

    [RequirePermission("template:write")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ContainerTemplate template, CancellationToken ct)
    {
        template.OwnerId = _currentUser.GetUserId();

        if (template.OrganizationId.HasValue && !_currentUser.IsAdmin())
        {
            var hasPermission = await _orgMembership.HasPermissionAsync(
                _currentUser.GetUserId(), template.OrganizationId.Value, Permissions.TemplateCreate, ct);
            if (!hasPermission) return Forbid();
        }

        _db.Templates.Add(template);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = template.Id }, template);
    }

    [RequirePermission("template:write")]
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ContainerTemplate update, CancellationToken ct)
    {
        var template = await _db.Templates.FindAsync([id], ct);
        if (template is null) return NotFound();
        if (!CanModifyTemplate(template)) return Forbid();

        template.Name = update.Name;
        template.Description = update.Description;
        template.Version = update.Version;
        template.IdeType = update.IdeType;
        template.Tags = update.Tags;
        // Conductor #886. ThemeId may be set, cleared, or left
        // unchanged via this endpoint. Validate against the
        // catalog when set — unknown id → 422.
        if (!string.IsNullOrEmpty(update.ThemeId))
        {
            var themeExists = await _db.Themes
                .AsNoTracking()
                .AnyAsync(t => t.Id == update.ThemeId, ct);
            if (!themeExists)
            {
                return UnprocessableEntity(new
                {
                    code = "unknown_theme",
                    message = $"Theme '{update.ThemeId}' is not in the catalog.",
                });
            }
        }
        template.ThemeId = update.ThemeId;
        template.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(template);
    }

    [RequirePermission("template:write")]
    [HttpPost("{id:guid}/publish")]
    public async Task<IActionResult> Publish(Guid id, CancellationToken ct)
    {
        var template = await _db.Templates.FindAsync([id], ct);
        if (template is null) return NotFound();
        if (!CanModifyTemplate(template)) return Forbid();

        template.IsPublished = true;
        template.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [RequirePermission("template:delete")]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var template = await _db.Templates.FindAsync([id], ct);
        if (template is null) return NotFound();
        if (!CanModifyTemplate(template)) return Forbid();

        _db.Templates.Remove(template);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [RequirePermission("template:write")]
    [HttpPost("validate")]
    public IActionResult Validate([FromBody] YamlContentRequest request)
    {
        var result = _parser.Validate(request.Content);
        return Ok(result);
    }

    [RequirePermission("template:write")]
    [HttpPost("from-yaml")]
    public async Task<IActionResult> CreateFromYaml([FromBody] YamlContentRequest request, CancellationToken ct)
    {
        var validation = _parser.Validate(request.Content);
        if (!validation.IsValid)
            return BadRequest(validation);

        var template = _parser.Parse(request.Content);
        template.OwnerId = _currentUser.GetUserId();

        // IM8 (#262). Compute the content-addressable spec hash so a
        // future build against this template can short-circuit when
        // the same spec has already been built. Files are not yet
        // wired through this JSON-only endpoint, so the hash is
        // computed against the canonical-JSON form alone (no file
        // digests). The multipart variant (per IM5's contract) will
        // mix file digests in once it lands as part of the
        // orchestration in Phase 1.
        template.SpecHash = ComputeSpecHash(template);

        // IM8 (#262). Idempotent re-register: if a template with the
        // same code AND the same spec hash already exists, return
        // that existing row instead of creating a duplicate. This is
        // what makes 'register the same spec twice' a no-op — the
        // contract documented in the IM5 OpenAPI for
        // RegisteredTemplate.created.
        var existing = await _db.Templates
            .FirstOrDefaultAsync(t => t.Code == template.Code, ct);
        if (existing is not null)
        {
            if (existing.SpecHash == template.SpecHash)
            {
                return Ok(new RegisteredTemplate(
                    TemplateId: existing.Id,
                    SpecHash: existing.SpecHash ?? string.Empty,
                    Created: false,
                    Code: existing.Code,
                    Name: existing.Name,
                    Version: existing.Version));
            }

            // IM10 (#264). 409 mapping moved into the shared
            // ImageManagementProblemDetailsFactory so the response
            // shape matches every other 4xx/5xx in this surface.
            return ImageManagementProblemDetailsFactory.FromCodeInUse(
                existing.Code, existing.Id);
        }

        _db.Templates.Add(template);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(
            nameof(Get),
            new { id = template.Id },
            new RegisteredTemplate(
                TemplateId: template.Id,
                SpecHash: template.SpecHash ?? string.Empty,
                Created: true,
                Code: template.Code,
                Name: template.Name,
                Version: template.Version));
    }

    /// <summary>
    /// Compute the IM3 content-addressable spec hash for a parsed
    /// template. Aligns with the formula in the architecture memo:
    /// <c>sha256(canonicalJson(parsedSpec) || sortedFileDigests)</c>.
    /// In the JSON-only register endpoint there are no uploaded
    /// files, so the file-digest map is empty.
    /// </summary>
    private static string ComputeSpecHash(ContainerTemplate template)
    {
        // Build a stable JSON projection of the template fields that
        // matter for the build outcome. Canonical-JSON normalisation
        // handles key ordering and whitespace; the projection just
        // needs to be deterministic and complete.
        var projection = new
        {
            code = template.Code,
            version = template.Version,
            base_image = template.BaseImage,
            extends = template.Extends,
            packages = SafeDeserialize(template.Packages),
            files = SafeDeserialize(template.Files),
            install = SafeDeserialize(template.Install),
            entrypoint = template.EntryPoint,
            markers = SafeDeserialize(template.Markers),
            // Toolchains (the existing dependency model) is part of
            // the template too; include it so changes to dependencies
            // bust the cache the same way changes to imperative
            // fields do.
            toolchains = SafeDeserialize(template.Toolchains),
        };
        var json = JsonSerializer.Serialize(projection);
        var canonical = CanonicalJson.Serialize(json);
        return CanonicalJson.ComputeSpecHash(
            canonical,
            new Dictionary<string, string>());
    }

    private static object? SafeDeserialize(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<JsonElement>(json);

    /// <summary>
    /// Response shape for <c>POST /api/templates/from-yaml</c>
    /// matching the IM5 OpenAPI contract.
    /// </summary>
    public sealed record RegisteredTemplate(
        Guid TemplateId,
        string SpecHash,
        bool Created,
        string Code,
        string Name,
        string Version);

    [RequirePermission("template:write")]
    [HttpPut("{id:guid}/definition")]
    public async Task<IActionResult> UpdateDefinition(Guid id, [FromBody] YamlContentRequest request, CancellationToken ct)
    {
        var template = await _db.Templates.FindAsync([id], ct);
        if (template is null) return NotFound();
        if (!CanModifyTemplate(template)) return Forbid();

        var validation = _parser.Validate(request.Content);
        if (!validation.IsValid)
            return BadRequest(validation);

        var parsed = _parser.Parse(request.Content);
        template.Name = parsed.Name;
        template.Description = parsed.Description;
        template.Version = parsed.Version;
        template.BaseImage = parsed.BaseImage;
        template.IdeType = parsed.IdeType;
        template.CatalogScope = parsed.CatalogScope;
        template.GpuRequired = parsed.GpuRequired;
        template.GpuPreferred = parsed.GpuPreferred;
        template.Tags = parsed.Tags;
        template.Ports = parsed.Ports;
        template.EnvironmentVariables = parsed.EnvironmentVariables;
        template.Scripts = parsed.Scripts;
        template.DefaultResources = parsed.DefaultResources;
        template.Toolchains = parsed.Toolchains;
        template.GitRepositories = parsed.GitRepositories;
        template.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(template);
    }

    private bool CanModifyTemplate(ContainerTemplate template)
    {
        if (_currentUser.IsAdmin()) return true;
        // Global templates can only be modified by admins
        if (template.CatalogScope == CatalogScope.Global) return false;
        // User-scoped templates can be modified by their owner
        return template.OwnerId == _currentUser.GetUserId();
    }

    [RequirePermission("template:read")]
    [HttpGet("{code}/image-status")]
    public async Task<IActionResult> GetImageStatus(string code, CancellationToken ct)
    {
        var record = await _buildService.GetBuildStatusAsync(code, ct);
        if (record is null)
            return Ok(new { status = "none", message = "Not a custom image template" });
        return Ok(record);
    }

    [RequirePermission("template:read")]
    [HttpGet("image-statuses")]
    public async Task<IActionResult> GetAllImageStatuses(CancellationToken ct)
    {
        var records = await _buildService.GetAllBuildStatusesAsync(ct);
        return Ok(records);
    }

    [RequirePermission("template:write")]
    [HttpPost("{code}/build-image")]
    public async Task<IActionResult> BuildImage(string code, CancellationToken ct)
    {
        try
        {
            var record = await _buildService.TriggerBuildAsync(code, ct);
            return Accepted(record);
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

public record YamlContentRequest(string Content);
