using Andy.Containers.Models.ImageManagement;
using Andy.Containers.Storage;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Infrastructure.Data;

/// <summary>
/// EF Core implementation of <see cref="IBuildArtifactStore"/>. Operates
/// against <see cref="ContainersDbContext"/>; honours its DbContext
/// scope (one instance per request in the API host, one per test).
/// </summary>
public sealed class BuildArtifactStore : IBuildArtifactStore
{
    private readonly ContainersDbContext _db;

    public BuildArtifactStore(ContainersDbContext db)
    {
        _db = db;
    }

    public Task<BuildArtifactEntity?> GetByDigestAsync(string digest, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        return _db.BuildArtifacts
            .Include(b => b.References)
            .FirstOrDefaultAsync(b => b.Digest == digest, ct);
    }

    public Task<BuildArtifactEntity?> GetBySpecHashAsync(
        Guid templateId,
        string specHash,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specHash);
        return _db.BuildArtifacts
            .Include(b => b.References)
            .FirstOrDefaultAsync(
                b => b.TemplateId == templateId && b.SpecHash == specHash,
                ct);
    }

    public async Task<BuildArtifactEntity> AddAsync(
        BuildArtifactEntity artifact,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        _db.BuildArtifacts.Add(artifact);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return artifact;
    }

    public async Task<RegistryReferenceEntity> AddReferenceAsync(
        Guid artifactId,
        RegistryReferenceEntity reference,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        // Ensure the artifact exists; otherwise the EF FK constraint
        // would fire on save with a less-helpful error.
        var exists = await _db.BuildArtifacts.AnyAsync(b => b.Id == artifactId, ct);
        if (!exists)
        {
            throw new KeyNotFoundException(
                $"No BuildArtifactEntity with Id '{artifactId}'. The artifact must be persisted before references can point at it.");
        }
        reference.BuildArtifactId = artifactId;
        if (reference.Id == Guid.Empty)
        {
            reference.Id = Guid.NewGuid();
        }
        _db.RegistryReferences.Add(reference);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return reference;
    }

    public async Task RemoveReferenceAsync(Guid referenceId, CancellationToken ct)
    {
        var entity = await _db.RegistryReferences.FirstOrDefaultAsync(r => r.Id == referenceId, ct);
        if (entity is null)
        {
            // Idempotent: removing an already-gone reference is not an error.
            return;
        }
        _db.RegistryReferences.Remove(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RegistryReferenceEntity>> ListReferencesAsync(
        Guid artifactId,
        CancellationToken ct)
    {
        return await _db.RegistryReferences
            .Where(r => r.BuildArtifactId == artifactId)
            .OrderBy(r => r.PushedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<BuildArtifactEntity> Items, int TotalCount)> ListAsync(
        Guid? templateId,
        string? registryId,
        int skip,
        int take,
        CancellationToken ct)
    {
        // Build the filtered query once; reuse for the count + page so
        // a paged response with totalCount mirrors what an unfiltered
        // SELECT COUNT(*) WHERE … would produce.
        var query = _db.BuildArtifacts.AsQueryable();
        if (templateId.HasValue)
        {
            query = query.Where(b => b.TemplateId == templateId.Value);
        }
        if (!string.IsNullOrWhiteSpace(registryId))
        {
            // Match artifacts whose reference set includes the registry.
            // The translation lands as an EXISTS subquery in the
            // generated SQL.
            query = query.Where(b => b.References.Any(r => r.RegistryId == registryId));
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(b => b.BuiltAt)
            .Skip(Math.Max(0, skip))
            .Take(Math.Max(0, take))
            .Include(b => b.References)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return (items, total);
    }
}
