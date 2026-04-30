using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Andy.Rbac.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Andy.Containers.Api.Controllers;

/// <summary>
/// Conductor #886. Read-only catalog endpoint for predefined
/// visual themes seeded from <c>config/themes/global/*.yaml</c>.
///
/// Why read-only in v1: themes are operator-curated artefacts —
/// adding one means landing a YAML file via PR, not a runtime
/// API call. User-defined themes are explicitly out of scope.
/// </summary>
[ApiController]
[Route("api/themes")]
[Authorize]
public class ThemesController : ControllerBase
{
    private readonly ContainersDbContext _db;
    private readonly ILogger<ThemesController> _logger;

    public ThemesController(ContainersDbContext db, ILogger<ThemesController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Returns the full theme catalog. Optionally filter by
    /// <paramref name="kind"/> (<c>terminal</c>, <c>ide</c>,
    /// <c>vnc</c>) so the picker UI doesn't have to filter
    /// client-side. Unknown filter returns an empty list rather
    /// than 400 — the catalog is still authoritative.
    /// </summary>
    [HttpGet]
    [RequirePermission("container:read")]
    public async Task<IActionResult> List([FromQuery] string? kind, CancellationToken ct)
    {
        IQueryable<Theme> query = _db.Themes.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(kind))
        {
            query = query.Where(t => t.Kind == kind);
        }

        var rows = await query
            .OrderBy(t => t.DisplayName)
            .ToListAsync(ct);

        // Project to the wire shape — palette comes back as a
        // parsed dictionary so the Conductor client doesn't have
        // to double-decode the JSON envelope.
        var dtos = rows
            .Select(t => new ThemeDto(
                t.Id,
                t.Name,
                t.DisplayName,
                t.Kind,
                ParsePalette(t.PaletteJson),
                t.Version
            ))
            .ToList();

        return Ok(new { items = dtos });
    }

    private Dictionary<string, string> ParsePalette(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "ThemesController: failed to parse palette JSON; returning empty palette. Theme rows are seeded from YAML so a parse failure here usually means the YAML was malformed.");
            return new Dictionary<string, string>();
        }
    }
}

/// <summary>
/// Wire shape for catalog responses. Kept distinct from the
/// EF entity so palette comes back as a dictionary instead of a
/// raw JSON string.
/// </summary>
public record ThemeDto(
    string Id,
    string Name,
    string DisplayName,
    string Kind,
    Dictionary<string, string> Palette,
    int Version
);
