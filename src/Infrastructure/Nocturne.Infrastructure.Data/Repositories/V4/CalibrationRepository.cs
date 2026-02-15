using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

public class CalibrationRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<CalibrationRepository> _logger;

    public CalibrationRepository(NocturneDbContext context, ILogger<CalibrationRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IEnumerable<Calibration>> GetAsync(
        long? from, long? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
    {
        var query = _context.Calibrations.AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Mills <= to.Value);
        if (device != null) query = query.Where(e => e.Device == device);
        if (source != null) query = query.Where(e => e.DataSource == source);
        query = descending ? query.OrderByDescending(e => e.Mills) : query.OrderBy(e => e.Mills);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(CalibrationMapper.ToDomainModel);
    }

    public async Task<Calibration?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.Calibrations.FindAsync([id], ct);
        return entity is null ? null : CalibrationMapper.ToDomainModel(entity);
    }

    public async Task<Calibration?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.Calibrations.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : CalibrationMapper.ToDomainModel(entity);
    }

    public async Task<Calibration> CreateAsync(Calibration model, CancellationToken ct = default)
    {
        var entity = CalibrationMapper.ToEntity(model);
        _context.Calibrations.Add(entity);
        await _context.SaveChangesAsync(ct);
        return CalibrationMapper.ToDomainModel(entity);
    }

    public async Task<Calibration> UpdateAsync(Guid id, Calibration model, CancellationToken ct = default)
    {
        var entity = await _context.Calibrations.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Calibration {id} not found");
        CalibrationMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return CalibrationMapper.ToDomainModel(entity);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.Calibrations.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Calibration {id} not found");
        _context.Calibrations.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<int> CountAsync(long? from, long? to, CancellationToken ct = default)
    {
        var query = _context.Calibrations.AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Mills <= to.Value);
        return await query.CountAsync(ct);
    }

    public async Task<IEnumerable<Calibration>> GetByCorrelationIdAsync(Guid correlationId, CancellationToken ct = default)
    {
        var entities = await _context.Calibrations
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(CalibrationMapper.ToDomainModel);
    }
}
