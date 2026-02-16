using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

public class SensorGlucoseRepository : ISensorGlucoseRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<SensorGlucoseRepository> _logger;

    public SensorGlucoseRepository(NocturneDbContext context, ILogger<SensorGlucoseRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IEnumerable<SensorGlucose>> GetAsync(
        long? from, long? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
    {
        var query = _context.SensorGlucose.AsNoTracking().AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Mills <= to.Value);
        if (device != null) query = query.Where(e => e.Device == device);
        if (source != null) query = query.Where(e => e.DataSource == source);
        query = descending ? query.OrderByDescending(e => e.Mills) : query.OrderBy(e => e.Mills);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(SensorGlucoseMapper.ToDomainModel);
    }

    public async Task<SensorGlucose?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.SensorGlucose.FindAsync([id], ct);
        return entity is null ? null : SensorGlucoseMapper.ToDomainModel(entity);
    }

    public async Task<SensorGlucose?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.SensorGlucose.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : SensorGlucoseMapper.ToDomainModel(entity);
    }

    public async Task<SensorGlucose> CreateAsync(SensorGlucose model, CancellationToken ct = default)
    {
        var entity = SensorGlucoseMapper.ToEntity(model);
        _context.SensorGlucose.Add(entity);
        await _context.SaveChangesAsync(ct);
        return SensorGlucoseMapper.ToDomainModel(entity);
    }

    public async Task<SensorGlucose> UpdateAsync(Guid id, SensorGlucose model, CancellationToken ct = default)
    {
        var entity = await _context.SensorGlucose.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"SensorGlucose {id} not found");
        SensorGlucoseMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return SensorGlucoseMapper.ToDomainModel(entity);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.SensorGlucose.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"SensorGlucose {id} not found");
        _context.SensorGlucose.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<int> CountAsync(long? from, long? to, CancellationToken ct = default)
    {
        var query = _context.SensorGlucose.AsNoTracking().AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Mills <= to.Value);
        return await query.CountAsync(ct);
    }

    public async Task<IEnumerable<SensorGlucose>> GetByCorrelationIdAsync(Guid correlationId, CancellationToken ct = default)
    {
        var entities = await _context.SensorGlucose
            .AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(SensorGlucoseMapper.ToDomainModel);
    }

    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        return await _context.SensorGlucose
            .Where(e => e.LegacyId == legacyId)
            .ExecuteDeleteAsync(ct);
    }
}
