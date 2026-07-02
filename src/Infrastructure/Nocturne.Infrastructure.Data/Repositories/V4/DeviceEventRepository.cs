using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing device event records in the database. A DeduplicationService participant,
/// so it inherits the shared CRUD/soft-delete surface from
/// <see cref="V4RepositoryBase{TModel,TEntity}"/> and keeps only the dedup-specific behaviour as
/// overrides (extended <c>GetAsync</c> with the non-primary LinkedRecords filter, dedup
/// <c>BulkCreateAsync</c>, audited soft-deletes) plus the event-type query helpers.
/// </summary>
public class DeviceEventRepository : V4RepositoryBase<DeviceEvent, DeviceEventEntity>, IDeviceEventRepository
{
    private readonly IDeduplicationService _deduplicationService;
    private readonly ILogger<DeviceEventRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceEventRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="deduplicationService">The deduplication service.</param>
    /// <param name="auditContext">The audit context for tracking mutations.</param>
    /// <param name="logger">The logger instance.</param>
    public DeviceEventRepository(
        ITenantDbContextFactory contextFactory,
        IDeduplicationService deduplicationService,
        IAuditContext auditContext,
        ILogger<DeviceEventRepository> logger,
        IV4RecordBroadcaster<DeviceEvent>? broadcaster = null)
        : base(contextFactory, auditContext, broadcaster)
    {
        _deduplicationService = deduplicationService;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override DeviceEventEntity ToEntity(DeviceEvent model) => DeviceEventMapper.ToEntity(model);

    /// <inheritdoc />
    protected override DeviceEvent ToDomain(DeviceEventEntity entity) => DeviceEventMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(DeviceEventEntity target, DeviceEvent source) => DeviceEventMapper.UpdateEntity(target, source);

    /// <summary>
    /// Excludes non-primary cross-connector duplicates so <see cref="V4RepositoryBase{TModel,TEntity}.CountAsync"/>
    /// matches the rows <c>GetAsync</c> returns. Mirrors the inline filter in the extended <c>GetAsync</c>.
    /// </summary>
    protected override IQueryable<DeviceEventEntity> ApplyReadVisibility(IQueryable<DeviceEventEntity> query, NocturneDbContext ctx) =>
        query.Where(b => !ctx.LinkedRecords.Any(lr => lr.RecordType == "deviceevent" && !lr.IsPrimary && lr.RecordId == b.Id));

    /// <summary>
    /// Routes the base 7-arg form through the extended device-event query (non-primary LinkedRecords
    /// exclusion + ordering), preserving the pre-base default-interface bridge behaviour.
    /// </summary>
    public override Task<IEnumerable<DeviceEvent>> GetAsync(
        DateTime? from, DateTime? to, string? device, string? source,
        int limit = 100, int offset = 0, bool descending = true,
        CancellationToken ct = default)
        => GetAsync(from, to, device, source, limit, offset, descending, nativeOnly: false, ct);

    /// <summary>
    /// Gets device event records based on filter criteria.
    /// Deduplicates records using the <see cref="IDeduplicationService"/>.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by timestamp in descending order.</param>
    /// <param name="nativeOnly">Whether to return only native records.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of device events.</returns>
    public async Task<IEnumerable<DeviceEvent>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        bool nativeOnly = false,
        CancellationToken ct = default
    )
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var query = ctx.DeviceEvents.AsNoTracking().AsQueryable();
        if (from.HasValue)
            query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.Timestamp <= to.Value);
        if (device != null)
            query = query.Where(e => e.Device == device);
        if (source != null)
            query = query.Where(e => e.DataSource == source);
        if (nativeOnly)
            query = query.Where(e => e.LegacyId == null);

        // Exclude non-primary duplicates from cross-connector deduplication
        query = query.Where(b => !ctx.LinkedRecords
            .Any(lr => lr.RecordType == "deviceevent" && !lr.IsPrimary && lr.RecordId == b.Id));

        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return entities.Select(DeviceEventMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets device event records by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The correlation identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of device events.</returns>
    public async Task<IEnumerable<DeviceEvent>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    )
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx
            .DeviceEvents.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(DeviceEventMapper.ToDomainModel);
    }

    /// <summary>
    /// Deletes device event records matching the given data source and sync identifier.
    /// </summary>
    /// <param name="dataSource">The external data source name.</param>
    /// <param name="syncIdentifier">The external sync identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteBySyncIdentifierAsync(string dataSource, string syncIdentifier, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.AuditedSoftDeleteAsync(
            ctx.DeviceEvents.Where(e => e.DataSource == dataSource && e.SyncIdentifier == syncIdentifier),
            AuditContext, ct);
    }

    /// <summary>
    /// Insert-time deduplication: link saved records to canonical groups (runs after commit).
    /// </summary>
    protected override async Task PostCommitDedupAsync(
        NocturneDbContext ctx, IReadOnlyList<DeviceEventEntity> inserted, WriteOrigin origin, CancellationToken ct)
    {
        if (inserted.Count == 0)
            return;

        try
        {
            var dedupInputs = inserted.Select(e => new DeduplicationInput(
                RecordId: e.Id,
                Mills: new DateTimeOffset(e.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                DataSource: e.DataSource ?? "unknown",
                Criteria: new MatchCriteria { EventType = e.EventType }
            )).ToList();

            await _deduplicationService.DeduplicateBatchAsync(RecordType.DeviceEvent, dedupInputs, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to deduplicate {Type} batch of {Count}", "DeviceEvent", inserted.Count);
        }
    }

    /// <summary>
    /// Gets the latest device event of a specific type.
    /// </summary>
    /// <param name="eventType">The type of device event.</param>
    /// <param name="asOf">Optional upper bound on event timestamp; <c>null</c> means latest.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The latest device event, or null if none found.</returns>
    public async Task<DeviceEvent?> GetLatestByEventTypeAsync(DeviceEventType eventType, DateTime? asOf, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var eventTypeString = eventType.ToString();
        var query = ctx.DeviceEvents
            .AsNoTracking()
            .Where(e => e.EventType == eventTypeString);
        if (asOf is { } cutoff)
            query = query.Where(e => e.Timestamp <= cutoff);

        var entity = await query
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : DeviceEventMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets the latest device event from a set of event types.
    /// </summary>
    /// <param name="eventTypes">The types of device events to search for.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The latest device event, or null if none found.</returns>
    public async Task<DeviceEvent?> GetLatestByEventTypesAsync(DeviceEventType[] eventTypes, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var eventTypeStrings = eventTypes.Select(t => t.ToString()).ToList();
        var entity = await ctx.DeviceEvents
            .AsNoTracking()
            .Where(e => eventTypeStrings.Contains(e.EventType))
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : DeviceEventMapper.ToDomainModel(entity);
    }
}
