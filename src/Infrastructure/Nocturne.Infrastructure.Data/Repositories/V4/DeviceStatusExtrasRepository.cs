using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing device status extras in the database.
/// </summary>
public class DeviceStatusExtrasRepository : IDeviceStatusExtrasRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<DeviceStatusExtrasRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceStatusExtrasRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="logger">The logger instance.</param>
    public DeviceStatusExtrasRepository(NocturneDbContext context, ILogger<DeviceStatusExtrasRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new device status extras record.
    /// </summary>
    /// <param name="model">The device status extras to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created device status extras.</returns>
    public async Task<DeviceStatusExtras> CreateAsync(DeviceStatusExtras model, CancellationToken ct = default)
    {
        var entity = DeviceStatusExtrasMapper.ToEntity(model);
        _context.DeviceStatusExtras.Add(entity);
        await _context.SaveChangesAsync(ct);
        return DeviceStatusExtrasMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets device status extras records by correlation IDs.
    /// </summary>
    /// <param name="correlationIds">The correlation IDs to match.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Matching device status extras records.</returns>
    public async Task<IEnumerable<DeviceStatusExtras>> GetByCorrelationIdsAsync(
        IEnumerable<Guid> correlationIds, CancellationToken ct = default)
    {
        var ids = correlationIds.ToList();
        if (ids.Count == 0) return [];

        var entities = await _context.DeviceStatusExtras
            .AsNoTracking()
            .Where(e => ids.Contains(e.CorrelationId))
            .ToListAsync(ct);
        return entities.Select(DeviceStatusExtrasMapper.ToDomainModel);
    }

    /// <summary>
    /// Deletes device status extras records by correlation ID.
    /// </summary>
    /// <param name="correlationId">The correlation ID to match.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteByCorrelationIdAsync(Guid correlationId, CancellationToken ct = default)
    {
        return await _context.DeviceStatusExtras
            .Where(e => e.CorrelationId == correlationId)
            .ExecuteDeleteAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<DeviceStatusExtras>> BulkCreateAsync(
        IEnumerable<DeviceStatusExtras> records,
        CancellationToken ct = default)
    {
        var entities = records.Select(DeviceStatusExtrasMapper.ToEntity).ToList();
        if (entities.Count == 0)
            return [];

        // Batch-level dedup: keep first occurrence per CorrelationId
        entities = entities
            .GroupBy(e => e.CorrelationId)
            .Select(g => g.First())
            .ToList();

        // DB-level dedup: filter out records whose CorrelationId already exists
        var correlationIds = entities
            .Select(e => e.CorrelationId)
            .ToHashSet();

        if (correlationIds.Count > 0)
        {
            var existingIds = await _context
                .DeviceStatusExtras.AsNoTracking()
                .Where(e => correlationIds.Contains(e.CorrelationId))
                .Select(e => e.CorrelationId)
                .ToListAsync(ct);

            var existingSet = existingIds.ToHashSet();
            entities = entities
                .Where(e => !existingSet.Contains(e.CorrelationId))
                .ToList();
        }

        if (entities.Count == 0)
            return [];

        const int batchSize = 500;
        foreach (var batch in entities.Chunk(batchSize))
        {
            _context.DeviceStatusExtras.AddRange(batch);
            await _context.SaveChangesAsync(ct);
            _context.ChangeTracker.Clear();
        }

        return entities.Select(DeviceStatusExtrasMapper.ToDomainModel);
    }
}
