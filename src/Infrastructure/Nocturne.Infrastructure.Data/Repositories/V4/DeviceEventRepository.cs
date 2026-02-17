using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

public class DeviceEventRepository : IDeviceEventRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<DeviceEventRepository> _logger;

    public DeviceEventRepository(NocturneDbContext context, ILogger<DeviceEventRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IEnumerable<DeviceEvent>> GetAsync(
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
        var query = _context.DeviceEvents.AsNoTracking().AsQueryable();
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
        return entities.Select(DeviceEventMapper.ToDomainModel);
    }

    public async Task<DeviceEvent?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.DeviceEvents.FindAsync([id], ct);
        return entity is null ? null : DeviceEventMapper.ToDomainModel(entity);
    }

    public async Task<DeviceEvent?> GetByLegacyIdAsync(
        string legacyId,
        CancellationToken ct = default
    )
    {
        var entity = await _context.DeviceEvents.FirstOrDefaultAsync(
            e => e.LegacyId == legacyId,
            ct
        );
        return entity is null ? null : DeviceEventMapper.ToDomainModel(entity);
    }

    public async Task<DeviceEvent> CreateAsync(DeviceEvent model, CancellationToken ct = default)
    {
        var entity = DeviceEventMapper.ToEntity(model);
        _context.DeviceEvents.Add(entity);
        await _context.SaveChangesAsync(ct);
        return DeviceEventMapper.ToDomainModel(entity);
    }

    public async Task<DeviceEvent> UpdateAsync(
        Guid id,
        DeviceEvent model,
        CancellationToken ct = default
    )
    {
        var entity =
            await _context.DeviceEvents.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"DeviceEvent {id} not found");
        DeviceEventMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return DeviceEventMapper.ToDomainModel(entity);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity =
            await _context.DeviceEvents.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"DeviceEvent {id} not found");
        _context.DeviceEvents.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<int> CountAsync(long? from, long? to, CancellationToken ct = default)
    {
        var query = _context.DeviceEvents.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Mills >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Mills <= to.Value);
        return await query.CountAsync(ct);
    }

    public async Task<IEnumerable<DeviceEvent>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    )
    {
        var entities = await _context
            .DeviceEvents.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(DeviceEventMapper.ToDomainModel);
    }

    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        return await _context
            .DeviceEvents.Where(e => e.LegacyId == legacyId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<IEnumerable<DeviceEvent>> BulkCreateAsync(
        IEnumerable<DeviceEvent> records,
        CancellationToken ct = default
    )
    {
        var entities = records.Select(DeviceEventMapper.ToEntity).ToList();
        _context.DeviceEvents.AddRange(entities);
        await _context.SaveChangesAsync(ct);
        return entities.Select(DeviceEventMapper.ToDomainModel);
    }
}
