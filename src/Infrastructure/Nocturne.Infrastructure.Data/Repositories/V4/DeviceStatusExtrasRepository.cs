using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing device status extras in the database.
/// </summary>
public class DeviceStatusExtrasRepository : IDeviceStatusExtrasRepository
{
    private readonly ITenantDbContextFactory _contextFactory;
    private readonly ILogger<DeviceStatusExtrasRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceStatusExtrasRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="logger">The logger instance.</param>
    public DeviceStatusExtrasRepository(ITenantDbContextFactory contextFactory, ILogger<DeviceStatusExtrasRepository> logger)
    {
        _contextFactory = contextFactory;
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = DeviceStatusExtrasMapper.ToEntity(model);
        ctx.DeviceStatusExtras.Add(entity);
        await ctx.SaveChangesAsync(ct);
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

        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entities = await ctx.DeviceStatusExtras
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
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx.DeviceStatusExtras
            .Where(e => e.CorrelationId == correlationId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.DeletedAt, DateTime.UtcNow), ct);
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

        await using var ctx = await _contextFactory.CreateAsync(ct);
        var strategy = ctx.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await ctx.Database.BeginTransactionAsync(ct);

            if (correlationIds.Count > 0)
            {
                var existingRecords = await ctx.DeviceStatusExtras.IgnoreQueryFilters().AsNoTracking()
                    .Where(e => e.TenantId == ctx.TenantId)
                    .Where(e => correlationIds.Contains(e.CorrelationId))
                    .Select(e => new { e.CorrelationId, IsSoftDeleted = e.DeletedAt != null })
                    .ToListAsync(ct);

                var existingSet = existingRecords.Select(r => r.CorrelationId).ToHashSet();
                var softDeletedCount = existingRecords.Count(r => r.IsSoftDeleted);

                if (softDeletedCount > 0)
                    _logger.LogInformation(
                        "Skipped {Count} previously-deleted {Type} records during import",
                        softDeletedCount, "DeviceStatusExtras");

                entities = entities
                    .Where(e => !existingSet.Contains(e.CorrelationId))
                    .ToList();
            }

            if (entities.Count == 0)
            {
                await tx.CommitAsync(ct);
                return [];
            }

            const int batchSize = 500;
            foreach (var batch in entities.Chunk(batchSize))
            {
                ctx.DeviceStatusExtras.AddRange(batch);
                await ctx.SaveChangesAsync(ct);
                ctx.ChangeTracker.Clear();
            }

            await tx.CommitAsync(ct);
            return entities.Select(DeviceStatusExtrasMapper.ToDomainModel);
        });
    }
}
