using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Andy.Containers.Api.Services;
using Andy.Containers.Crypto;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Andy.Rbac.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
    [Consumes("application/json")]
    public Task<IActionResult> CreateFromYaml([FromBody] YamlContentRequest request, CancellationToken ct)
        // JSON path: no uploaded files, so the file-digests dict is
        // empty and `UploadedFilesPath` stays null. Templates registered
        // through this path can't reference `files:` entries — that
        // requires the multipart variant below.
        => RegisterFromYamlAsync(
            yaml: request.Content,
            fileDigests: new Dictionary<string, string>(),
            uploadedFilesPath: null,
            ct: ct);

    /// <summary>
    /// #277 (PR A). Multipart variant of <c>POST /api/templates/from-yaml</c>.
    ///
    /// Accepts a <c>spec</c> form field (YAML body) plus zero-or-more
    /// <c>files[<em>name</em>]</c> file parts. Each file is staged to
    /// <c>&lt;temp&gt;/andy-containers/template-uploads/&lt;templateId&gt;/&lt;name&gt;</c>
    /// and its SHA-256 digest is mixed into the IM3 spec hash, so two
    /// otherwise-identical specs differing only in file content produce
    /// different spec hashes and cache distinctly.
    ///
    /// Files staged here survive between the register call and the
    /// later <c>POST /api/images/{templateId}/build</c>, so the build
    /// backend can pick them up via <c>IBuildContext.Files</c>. The
    /// orchestrator-side wire-up (replacing <c>EmptyBuildContext</c>
    /// with a <c>StagedBuildContext</c> that reads from
    /// <c>UploadedFilesPath</c>) lands in PR B; cleanup (TTL or
    /// post-build delete) lands in PR C.
    /// </summary>
    [RequirePermission("template:write")]
    [HttpPost("from-yaml")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxMultipartRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxMultipartRequestBytes)]
    public async Task<IActionResult> CreateFromYamlMultipart(CancellationToken ct)
    {
        if (!Request.HasFormContentType)
        {
            return BadRequest(new { error = "Request body must be multipart/form-data." });
        }

        var form = await Request.ReadFormAsync(ct);

        // The YAML spec lives in a `spec` field; tolerate `content` as
        // a fallback so callers that mirror the JSON body's field name
        // also work.
        var yaml = form["spec"].ToString();
        if (string.IsNullOrWhiteSpace(yaml))
        {
            yaml = form["content"].ToString();
        }
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return BadRequest(new { error = "Multipart request is missing the `spec` field (YAML body)." });
        }

        // Stage each uploaded file under a deterministic path keyed
        // by a fresh staging id. The id is independent of the
        // templateId because the templateId isn't known until after
        // the YAML is parsed AND the idempotency check has run; once
        // we commit to a new template row, the staging dir is
        // renamed to its templateId. Idempotent re-registers reuse
        // the existing template's UploadedFilesPath instead.
        var stagingId = Guid.NewGuid();
        var stagingDir = Path.Combine(
            Path.GetTempPath(),
            "andy-containers",
            "template-uploads",
            "staging",
            stagingId.ToString("N"));

        var fileDigests = new Dictionary<string, string>(StringComparer.Ordinal);
        if (form.Files.Count > 0)
        {
            Directory.CreateDirectory(stagingDir);
            try
            {
                foreach (var file in form.Files)
                {
                    if (file.Length > MaxFileSizeBytes)
                    {
                        return BadRequest(new
                        {
                            error = $"Uploaded file `{file.Name}` exceeds the per-file limit of {MaxFileSizeBytes:N0} bytes.",
                        });
                    }

                    // Logical name == multipart part name. The Conductor
                    // client uses the spec's `files[].source` value as
                    // the part name, so this matches the spec entry
                    // verbatim.
                    var logicalName = file.Name;
                    var safeRelativePath = SanitiseLogicalName(logicalName);
                    if (safeRelativePath is null)
                    {
                        return BadRequest(new
                        {
                            error = $"Uploaded file part name `{logicalName}` is not a safe relative path.",
                        });
                    }

                    var dest = Path.Combine(stagingDir, safeRelativePath);
                    var destDir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    await using (var sink = System.IO.File.Create(dest))
                    {
                        await file.CopyToAsync(sink, ct);
                    }

                    fileDigests[logicalName] = await ComputeFileDigestAsync(dest, ct);
                }
            }
            catch
            {
                // Best-effort cleanup on any partial-write failure so
                // the staging area doesn't grow unbounded.
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
                throw;
            }
        }

        var result = await RegisterFromYamlAsync(
            yaml: yaml,
            fileDigests: fileDigests,
            uploadedFilesPath: form.Files.Count > 0 ? stagingDir : null,
            ct: ct);

        // If the call short-circuited (idempotent re-register or 409),
        // the freshly-staged files are unused — drop them so the temp
        // root doesn't accumulate one staging dir per duplicate POST.
        // The existing template's pre-existing UploadedFilesPath (if
        // any) is untouched.
        if (form.Files.Count > 0 && Directory.Exists(stagingDir))
        {
            var keep = result is CreatedAtActionResult { Value: RegisteredTemplate created } && created.Created;
            if (!keep)
            {
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
            }
        }

        return result;
    }

    /// <summary>
    /// Shared implementation of <c>POST /api/templates/from-yaml</c>
    /// for both content-type variants. Validates + parses the spec,
    /// computes the IM3 spec hash with optional file digests mixed in,
    /// and either:
    /// - returns the existing row when (code, specHash) match (200,
    ///   created=false), per IM8 idempotency,
    /// - returns 409 when code matches but specHash differs (per IM10),
    /// - or creates a new row and returns 201.
    /// </summary>
    private async Task<IActionResult> RegisterFromYamlAsync(
        string yaml,
        IReadOnlyDictionary<string, string> fileDigests,
        string? uploadedFilesPath,
        CancellationToken ct)
    {
        var validation = _parser.Validate(yaml);
        if (!validation.IsValid)
            return BadRequest(validation);

        var template = _parser.Parse(yaml);
        template.OwnerId = _currentUser.GetUserId();
        template.UploadedFilesPath = uploadedFilesPath;
        template.SpecHash = ComputeSpecHash(template, fileDigests);

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

    // #277 (PR A). Hard caps on the multipart upload, tunable when the
    // option-bag in PR C lands. Per the issue:
    // - 32 MiB per individual file
    // - 256 MiB total request size
    private const long MaxFileSizeBytes = 32L * 1024 * 1024;
    private const long MaxMultipartRequestBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Returns a relative path safe to combine with a staging root, or
    /// <c>null</c> if the input attempts directory traversal or
    /// references an absolute path. Logical names come from the
    /// multipart `files[<name>]` part name and are usually shaped
    /// like `install-assistants.sh` or `bin/foo.sh` — both are fine.
    /// </summary>
    private static string? SanitiseLogicalName(string logicalName)
    {
        if (string.IsNullOrEmpty(logicalName)) return null;
        if (Path.IsPathRooted(logicalName)) return null;
        if (logicalName.Contains("..", StringComparison.Ordinal)) return null;
        // Normalise separators so the same name on macOS/Linux/Windows
        // produces the same on-disk shape.
        var normalised = logicalName.Replace('\\', '/');
        if (normalised.StartsWith('/')) return null;
        return normalised;
    }

    /// <summary>
    /// Computes the IM3 file digest for a staged upload. The digest is
    /// mixed into the spec hash so two specs that differ only in their
    /// referenced file content cache distinctly. Format matches
    /// <c>BuildArtifactReference.Digest</c>: <c>sha256:&lt;64 hex&gt;</c>.
    /// </summary>
    private static async Task<string> ComputeFileDigestAsync(string path, CancellationToken ct)
    {
        await using var stream = System.IO.File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Compute the IM3 content-addressable spec hash for a parsed
    /// template. Aligns with the formula in the architecture memo:
    /// <c>sha256(canonicalJson(parsedSpec) || sortedFileDigests)</c>.
    /// </summary>
    /// <param name="template">Parsed template with all fields populated.</param>
    /// <param name="fileDigests">
    /// Digests of files uploaded alongside the template, keyed by
    /// logical name (the multipart <c>files[name]</c> part name).
    /// Pass an empty dictionary when no files were uploaded — the
    /// hash then degrades to <c>sha256(canonicalJson(parsedSpec))</c>,
    /// matching the JSON-only register path.
    /// </param>
    private static string ComputeSpecHash(
        ContainerTemplate template,
        IReadOnlyDictionary<string, string> fileDigests)
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
        return CanonicalJson.ComputeSpecHash(canonical, fileDigests);
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
