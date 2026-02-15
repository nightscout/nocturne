using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

public class CarbIntakeRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<CarbIntakeRepository> _logger;

    public CarbIntakeRepository(NocturneDbContext context, ILogger<CarbIntakeRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IEnumerable<CarbIntake>> GetAsync(
        long? from, long? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
    {
        var query = _context.CarbIntakes.AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Mills <= to.Value);
        if (device != null) query = query.Where(e => e.Device == device);
        if (source != null) query = query.Where(e => e.DataSource == source);
        query = descending ? query.OrderByDescending(e => e.Mills) : query.OrderBy(e => e.Mills);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(CarbIntakeMapper.ToDomainModel);
    }

    public async Task<CarbIntake?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.CarbIntakes.FindAsync([id], ct);
        return entity is null ? null : CarbIntakeMapper.ToDomainModel(entity);
    }

    public async Task<CarbIntake?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var entity = await _context.CarbIntakes.FirstOrDefaultAsync(e => e.LegacyId == legacyId, ct);
        return entity is null ? null : CarbIntakeMapper.ToDomainModel(entity);
    }

    public async Task<CarbIntake> CreateAsync(CarbIntake model, CancellationToken ct = default)
    {
        var entity = CarbIntakeMapper.ToEntity(model);
        _context.CarbIntakes.Add(entity);
        await _context.SaveChangesAsync(ct);
        return CarbIntakeMapper.ToDomainModel(entity);
    }

    public async Task<CarbIntake> UpdateAsync(Guid id, CarbIntake model, CancellationToken ct = default)
    {
        var entity = await _context.CarbIntakes.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"CarbIntake {id} not found");
        CarbIntakeMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return CarbIntakeMapper.ToDomainModel(entity);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.CarbIntakes.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"CarbIntake {id} not found");
        _context.CarbIntakes.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<int> CountAsync(long? from, long? to, CancellationToken ct = default)
    {
        var query = _context.CarbIntakes.AsQueryable();
        if (from.HasValue) query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Mills <= to.Value);
        return await query.CountAsync(ct);
    }

    public async Task<IEnumerable<CarbIntake>> GetByCorrelationIdAsync(Guid correlationId, CancellationToken ct = default)
    {
        var entities = await _context.CarbIntakes
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(CarbIntakeMapper.ToDomainModel);
    }
}
