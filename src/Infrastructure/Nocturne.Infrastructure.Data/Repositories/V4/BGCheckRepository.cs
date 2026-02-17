using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

public class BGCheckRepository : IBGCheckRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<BGCheckRepository> _logger;

    public BGCheckRepository(NocturneDbContext context, ILogger<BGCheckRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IEnumerable<BGCheck>> GetAsync(
        long? from,
        long? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        CancellationToken ct = default
    )
    {
        var query = _context.BGChecks.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Mills <= to.Value);
        if (device != null)
            query = query.Where(e => e.Device == device);
        if (source != null)
            query = query.Where(e => e.DataSource == source);
        query = descending ? query.OrderByDescending(e => e.Mills) : query.OrderBy(e => e.Mills);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(BGCheckMapper.ToDomainModel);
    }

    public async Task<BGCheck?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.BGChecks.FindAsync([id], ct);
        return entity is null ? null : BGCheckMapper.ToDomainModel(entity);
    }

    public async Task<BGCheck?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.BGChecks.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : BGCheckMapper.ToDomainModel(entity);
    }

    public async Task<BGCheck> CreateAsync(BGCheck model, CancellationToken ct = default)
    {
        var entity = BGCheckMapper.ToEntity(model);
        _context.BGChecks.Add(entity);
        await _context.SaveChangesAsync(ct);
        return BGCheckMapper.ToDomainModel(entity);
    }

    public async Task<BGCheck> UpdateAsync(Guid id, BGCheck model, CancellationToken ct = default)
    {
        var entity =
            await _context.BGChecks.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"BGCheck {id} not found");
        BGCheckMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return BGCheckMapper.ToDomainModel(entity);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity =
            await _context.BGChecks.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"BGCheck {id} not found");
        _context.BGChecks.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<int> CountAsync(long? from, long? to, CancellationToken ct = default)
    {
        var query = _context.BGChecks.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Mills <= to.Value);
        return await query.CountAsync(ct);
    }

    public async Task<IEnumerable<BGCheck>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    )
    {
        var entities = await _context
            .BGChecks.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(BGCheckMapper.ToDomainModel);
    }

    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        return await _context.BGChecks.Where(e => e.LegacyId == legacyId).ExecuteDeleteAsync(ct);
    }

    public async Task<IEnumerable<BGCheck>> BulkCreateAsync(
        IEnumerable<BGCheck> records,
        CancellationToken ct = default
    )
    {
        var entities = records.Select(BGCheckMapper.ToEntity).ToList();
        if (entities.Count == 0)
            return [];

        // Batch-level dedup: keep first occurrence per LegacyId
        entities = entities
            .GroupBy(e => e.LegacyId ?? e.Id.ToString())
            .Select(g => g.First())
            .ToList();

        // DB-level dedup: filter out records whose LegacyId already exists
        var legacyIds = entities
            .Where(e => !string.IsNullOrEmpty(e.LegacyId))
            .Select(e => e.LegacyId!)
            .ToHashSet();

        if (legacyIds.Count > 0)
        {
            var existingIds = await _context
                .BGChecks.AsNoTracking()
                .Where(e => legacyIds.Contains(e.LegacyId!))
                .Select(e => e.LegacyId)
                .ToListAsync(ct);

            var existingSet = existingIds.ToHashSet();
            entities = entities
                .Where(e => string.IsNullOrEmpty(e.LegacyId) || !existingSet.Contains(e.LegacyId))
                .ToList();
        }

        if (entities.Count == 0)
            return [];

        const int batchSize = 500;
        foreach (var batch in entities.Chunk(batchSize))
        {
            _context.BGChecks.AddRange(batch);
            await _context.SaveChangesAsync(ct);
            _context.ChangeTracker.Clear();
        }

        return entities.Select(BGCheckMapper.ToDomainModel);
    }
}
