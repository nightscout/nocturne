using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

public class BolusCalculationRepository : IBolusCalculationRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<BolusCalculationRepository> _logger;

    public BolusCalculationRepository(
        NocturneDbContext context,
        ILogger<BolusCalculationRepository> logger
    )
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IEnumerable<BolusCalculation>> GetAsync(
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
        var query = _context.BolusCalculations.AsNoTracking().AsQueryable();
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
        return entities.Select(BolusCalculationMapper.ToDomainModel);
    }

    public async Task<BolusCalculation?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.BolusCalculations.FindAsync([id], ct);
        return entity is null ? null : BolusCalculationMapper.ToDomainModel(entity);
    }

    public async Task<BolusCalculation?> GetByLegacyIdAsync(
        string legacyId,
        CancellationToken ct = default
    )
    {
        var entity = await _context.BolusCalculations.FirstOrDefaultAsync(
            e => e.LegacyId == legacyId,
            ct
        );
        return entity is null ? null : BolusCalculationMapper.ToDomainModel(entity);
    }

    public async Task<BolusCalculation> CreateAsync(
        BolusCalculation model,
        CancellationToken ct = default
    )
    {
        var entity = BolusCalculationMapper.ToEntity(model);
        _context.BolusCalculations.Add(entity);
        await _context.SaveChangesAsync(ct);
        return BolusCalculationMapper.ToDomainModel(entity);
    }

    public async Task<BolusCalculation> UpdateAsync(
        Guid id,
        BolusCalculation model,
        CancellationToken ct = default
    )
    {
        var entity =
            await _context.BolusCalculations.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"BolusCalculation {id} not found");
        BolusCalculationMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return BolusCalculationMapper.ToDomainModel(entity);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity =
            await _context.BolusCalculations.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"BolusCalculation {id} not found");
        _context.BolusCalculations.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<int> CountAsync(long? from, long? to, CancellationToken ct = default)
    {
        var query = _context.BolusCalculations.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Mills <= to.Value);
        return await query.CountAsync(ct);
    }

    public async Task<IEnumerable<BolusCalculation>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    )
    {
        var entities = await _context
            .BolusCalculations.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(BolusCalculationMapper.ToDomainModel);
    }

    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        return await _context
            .BolusCalculations.Where(e => e.LegacyId == legacyId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<IEnumerable<BolusCalculation>> BulkCreateAsync(
        IEnumerable<BolusCalculation> records,
        CancellationToken ct = default
    )
    {
        var entities = records.Select(BolusCalculationMapper.ToEntity).ToList();
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
                .BolusCalculations.AsNoTracking()
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
            _context.BolusCalculations.AddRange(batch);
            await _context.SaveChangesAsync(ct);
            _context.ChangeTracker.Clear();
        }

        return entities.Select(BolusCalculationMapper.ToDomainModel);
    }
}
