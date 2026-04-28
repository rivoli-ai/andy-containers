using System.ComponentModel;
using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Andy.Containers.Api.Mcp;

/// <summary>
/// X6 (rivoli-ai/andy-containers#96). MCP surface for the EnvironmentProfile
/// catalog. Mirrors the X3 HTTP endpoint at <c>GET /api/environments</c>:
/// MCP-aware clients (Conductor, Claude Code, agent runtimes) can discover
/// the available runtime shapes (headless / terminal / desktop) and their
/// capability envelopes without a separate HTTP round-trip.
/// </summary>
/// <remarks>
/// Discovered automatically by <c>WithToolsFromAssembly()</c> in
/// <c>Program.cs</c> — no explicit DI registration needed beyond the
/// services this class consumes.
/// </remarks>
[McpServerToolType]
public class EnvironmentsMcpTools
{
    private readonly ContainersDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IOrganizationMembershipService _orgMembership;
    private readonly ILogger<EnvironmentsMcpTools> _logger;

    public EnvironmentsMcpTools(
        ContainersDbContext db,
        ICurrentUserService currentUser,
        IOrganizationMembershipService orgMembership,
        ILogger<EnvironmentsMcpTools> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _orgMembership = orgMembership;
        _logger = logger;
    }

    [McpServerTool(Name = "environment.list"), Description(
        "List EnvironmentProfile rows from the catalog. Each profile declares the runtime shape " +
        "(headless / terminal / desktop), the base image used for provisioning, and the capability " +
        "envelope (network allowlist, secrets scope, GUI, audit mode). Optional kind filter narrows " +
        "to a single runtime shape. Requires environment:read.")]
    public async Task<IReadOnlyList<EnvironmentProfileDto>> ListEnvironments(
        [Description("Optional kind filter: 'HeadlessContainer', 'Terminal', or 'Desktop' (case-insensitive). Unknown values yield an empty result.")]
        string? kind = null,
        CancellationToken ct = default)
    {
        if (!await EnsurePermission(Permissions.EnvironmentRead, ct))
        {
            return Array.Empty<EnvironmentProfileDto>();
        }

        EnvironmentKind? kindFilter = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse<EnvironmentKind>(kind, ignoreCase: true, out var parsed))
            {
                // MCP clients get an empty list for unknown kinds rather
                // than an error envelope — the HTTP layer surfaces 400
                // for the same case (typo vs. empty), but the MCP
                // contract here is "list", and an empty list is the
                // right answer for "no profiles match this filter".
                _logger.LogDebug(
                    "environment.list: unknown kind filter '{Kind}'; returning empty.", kind);
                return Array.Empty<EnvironmentProfileDto>();
            }
            kindFilter = parsed;
        }

        var query = _db.EnvironmentProfiles.AsNoTracking().AsQueryable();
        if (kindFilter.HasValue)
        {
            query = query.Where(p => p.Kind == kindFilter.Value);
        }

        var rows = await query.OrderBy(p => p.Name).ToListAsync(ct);
        return rows.Select(EnvironmentProfileDto.FromEntity).ToList();
    }

    private async Task<bool> EnsurePermission(string permission, CancellationToken ct)
    {
        if (_currentUser.IsAdmin()) return true;

        var userId = _currentUser.GetUserId();
        if (string.IsNullOrEmpty(userId)) return false;

        var orgId = _currentUser.GetOrganizationId();
        if (orgId is null) return false;

        return await _orgMembership.HasPermissionAsync(userId, orgId.Value, permission, ct);
    }
}
