using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

public class UploaderSnapshotRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<UploaderSnapshotRepository> _logger;

    public UploaderSnapshotRepository(NocturneDbContext context, ILogger<UploaderSnapshotRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IEnumerable<UploaderSnapshot>> GetAsync(
        long? from, long? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
    {
        var query = _context.UploaderSnapshots.AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Mills <= to.Value);
        if (device != null) query = query.Where(e => e.Device == device);
        query = descending ? query.OrderByDescending(e => e.Mills) : query.OrderBy(e => e.Mills);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(UploaderSnapshotMapper.ToDomainModel);
    }

    public async Task<UploaderSnapshot?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.UploaderSnapshots.FindAsync([id], ct);
        return entity is null ? null : UploaderSnapshotMapper.ToDomainModel(entity);
    }

    public async Task<UploaderSnapshot?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.UploaderSnapshots.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : UploaderSnapshotMapper.ToDomainModel(entity);
    }

    public async Task<UploaderSnapshot> CreateAsync(UploaderSnapshot model, CancellationToken ct = default)
    {
        var entity = UploaderSnapshotMapper.ToEntity(model);
        _context.UploaderSnapshots.Add(entity);
        await _context.SaveChangesAsync(ct);
        return UploaderSnapshotMapper.ToDomainModel(entity);
    }

    public async Task<UploaderSnapshot> UpdateAsync(Guid id, UploaderSnapshot model, CancellationToken ct = default)
    {
        var entity = await _context.UploaderSnapshots.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"UploaderSnapshot {id} not found");
        UploaderSnapshotMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return UploaderSnapshotMapper.ToDomainModel(entity);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.UploaderSnapshots.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"UploaderSnapshot {id} not found");
        _context.UploaderSnapshots.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<int> CountAsync(long? from, long? to, CancellationToken ct = default)
    {
        var query = _context.UploaderSnapshots.AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Mills <= to.Value);
        return await query.CountAsync(ct);
    }
}
