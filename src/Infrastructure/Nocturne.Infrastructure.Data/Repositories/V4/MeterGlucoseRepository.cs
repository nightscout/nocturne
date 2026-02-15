using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

public class MeterGlucoseRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<MeterGlucoseRepository> _logger;

    public MeterGlucoseRepository(NocturneDbContext context, ILogger<MeterGlucoseRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IEnumerable<MeterGlucose>> GetAsync(
        long? from, long? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
    {
        var query = _context.MeterGlucose.AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Mills <= to.Value);
        if (device != null) query = query.Where(e => e.Device == device);
        if (source != null) query = query.Where(e => e.DataSource == source);
        query = descending ? query.OrderByDescending(e => e.Mills) : query.OrderBy(e => e.Mills);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(MeterGlucoseMapper.ToDomainModel);
    }

    public async Task<MeterGlucose?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.MeterGlucose.FindAsync([id], ct);
        return entity is null ? null : MeterGlucoseMapper.ToDomainModel(entity);
    }

    public async Task<MeterGlucose?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.MeterGlucose.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : MeterGlucoseMapper.ToDomainModel(entity);
    }

    public async Task<MeterGlucose> CreateAsync(MeterGlucose model, CancellationToken ct = default)
    {
        var entity = MeterGlucoseMapper.ToEntity(model);
        _context.MeterGlucose.Add(entity);
        await _context.SaveChangesAsync(ct);
        return MeterGlucoseMapper.ToDomainModel(entity);
    }

    public async Task<MeterGlucose> UpdateAsync(Guid id, MeterGlucose model, CancellationToken ct = default)
    {
        var entity = await _context.MeterGlucose.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"MeterGlucose {id} not found");
        MeterGlucoseMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return MeterGlucoseMapper.ToDomainModel(entity);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.MeterGlucose.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"MeterGlucose {id} not found");
        _context.MeterGlucose.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<int> CountAsync(long? from, long? to, CancellationToken ct = default)
    {
        var query = _context.MeterGlucose.AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Mills <= to.Value);
        return await query.CountAsync(ct);
    }

    public async Task<IEnumerable<MeterGlucose>> GetByCorrelationIdAsync(Guid correlationId, CancellationToken ct = default)
    {
        var entities = await _context.MeterGlucose
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(MeterGlucoseMapper.ToDomainModel);
    }
}
