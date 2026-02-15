using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

public class PumpSnapshotRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<PumpSnapshotRepository> _logger;

    public PumpSnapshotRepository(NocturneDbContext context, ILogger<PumpSnapshotRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IEnumerable<PumpSnapshot>> GetAsync(
        long? from, long? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
    {
        var query = _context.PumpSnapshots.AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Mills <= to.Value);
        if (device != null) query = query.Where(e => e.Device == device);
        query = descending ? query.OrderByDescending(e => e.Mills) : query.OrderBy(e => e.Mills);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(PumpSnapshotMapper.ToDomainModel);
    }

    public async Task<PumpSnapshot?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.PumpSnapshots.FindAsync([id], ct);
        return entity is null ? null : PumpSnapshotMapper.ToDomainModel(entity);
    }

    public async Task<PumpSnapshot?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.PumpSnapshots.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : PumpSnapshotMapper.ToDomainModel(entity);
    }

    public async Task<PumpSnapshot> CreateAsync(PumpSnapshot model, CancellationToken ct = default)
    {
        var entity = PumpSnapshotMapper.ToEntity(model);
        _context.PumpSnapshots.Add(entity);
        await _context.SaveChangesAsync(ct);
        return PumpSnapshotMapper.ToDomainModel(entity);
    }

    public async Task<PumpSnapshot> UpdateAsync(Guid id, PumpSnapshot model, CancellationToken ct = default)
    {
        var entity = await _context.PumpSnapshots.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"PumpSnapshot {id} not found");
        PumpSnapshotMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return PumpSnapshotMapper.ToDomainModel(entity);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.PumpSnapshots.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"PumpSnapshot {id} not found");
        _context.PumpSnapshots.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<int> CountAsync(long? from, long? to, CancellationToken ct = default)
    {
        var query = _context.PumpSnapshots.AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Mills <= to.Value);
        return await query.CountAsync(ct);
    }
}
