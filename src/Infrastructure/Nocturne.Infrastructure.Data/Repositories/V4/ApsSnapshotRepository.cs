using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

public class ApsSnapshotRepository : IApsSnapshotRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<ApsSnapshotRepository> _logger;

    public ApsSnapshotRepository(NocturneDbContext context, ILogger<ApsSnapshotRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IEnumerable<ApsSnapshot>> GetAsync(
        long? from, long? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
    {
        var query = _context.ApsSnapshots.AsNoTracking().AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Mills <= to.Value);
        if (device != null) query = query.Where(e => e.Device == device);
        query = descending ? query.OrderByDescending(e => e.Mills) : query.OrderBy(e => e.Mills);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(ApsSnapshotMapper.ToDomainModel);
    }

    public async Task<ApsSnapshot?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.ApsSnapshots.FindAsync([id], ct);
        return entity is null ? null : ApsSnapshotMapper.ToDomainModel(entity);
    }

    public async Task<ApsSnapshot?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.ApsSnapshots.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : ApsSnapshotMapper.ToDomainModel(entity);
    }

    public async Task<ApsSnapshot> CreateAsync(ApsSnapshot model, CancellationToken ct = default)
    {
        var entity = ApsSnapshotMapper.ToEntity(model);
        _context.ApsSnapshots.Add(entity);
        await _context.SaveChangesAsync(ct);
        return ApsSnapshotMapper.ToDomainModel(entity);
    }

    public async Task<ApsSnapshot> UpdateAsync(Guid id, ApsSnapshot model, CancellationToken ct = default)
    {
        var entity = await _context.ApsSnapshots.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"ApsSnapshot {id} not found");
        ApsSnapshotMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return ApsSnapshotMapper.ToDomainModel(entity);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.ApsSnapshots.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"ApsSnapshot {id} not found");
        _context.ApsSnapshots.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<int> CountAsync(long? from, long? to, CancellationToken ct = default)
    {
        var query = _context.ApsSnapshots.AsNoTracking().AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Mills <= to.Value);
        return await query.CountAsync(ct);
    }

    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        return await _context.ApsSnapshots
            .Where(e => e.LegacyId == legacyId)
            .ExecuteDeleteAsync(ct);
    }
}
