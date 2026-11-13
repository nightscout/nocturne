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
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing device event records in the database. A deduplication participant on
/// top of the sync-key upsert of <see cref="SyncUpsertRepositoryBase{TModel,TEntity}"/>, so it keeps only
/// the <c>GetAsync</c> overload that adds a patient device to the base read and the event-type query helpers.
/// </summary>
public class DeviceEventRepository : SyncUpsertRepositoryBase<DeviceEvent, DeviceEventEntity>, IDeviceEventRepository
{
    private readonly IDeviceEventReactor? _reactor;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceEventRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="linker">Links committed inserts into their duplicate groups.</param>
    /// <param name="auditContext">The audit context for tracking mutations.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="broadcaster">Optional realtime broadcaster for native V4 record shapes.</param>
    /// <param name="reactor">Optional domain reaction to live creates and deletes — the tracker trigger.
    /// Null when the repository is constructed without DI, in which case writes simply run no reaction.</param>
    /// <param name="legacyReads">Optional; null clears no legacy read cache.</param>
    public DeviceEventRepository(
        INocturneDbContextFactory contextFactory,
        IDuplicateLinker linker,
        IAuditContext auditContext,
        ILogger<DeviceEventRepository> logger,
        IV4RecordBroadcaster<DeviceEvent>? broadcaster = null,
        IDeviceEventReactor? reactor = null,
        ILegacyReadCacheInvalidation? legacyReads = null)
        : base(contextFactory, auditContext, logger, broadcaster, legacyReads: legacyReads, linker: linker)
    {
        _reactor = reactor;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Device events are the one V4 type carrying a domain reaction: a site or sensor change advances
    /// the tracker instances configured to trigger on it. Failures are logged and swallowed — the event
    /// itself is already committed, and a tracker that misses a change is recoverable in a way that
    /// failing the ingest of the underlying record is not.
    /// </remarks>
    protected override async Task OnLiveCreatedAsync(IReadOnlyList<DeviceEvent> created, CancellationToken ct)
    {
        if (_reactor is null)
            return;

        try
        {
            await _reactor.OnCreatedAsync(created, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Device event reaction failed for {Count} created event(s)", created.Count);
        }
    }

    /// <inheritdoc />
    /// <remarks>Failures are swallowed for the reason <see cref="OnLiveCreatedAsync"/> gives.</remarks>
    protected override async Task OnLiveDeletedAsync(IReadOnlyList<DeviceEvent> deleted, CancellationToken ct)
    {
        if (_reactor is null)
            return;

        try
        {
            await _reactor.OnDeletedAsync(deleted, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Device event reaction failed for {Count} deleted event(s)", deleted.Count);
        }
    }

    /// <inheritdoc />
    public async Task<int> WithdrawBySyncIdentifierAsync(
        string dataSource, string syncIdentifier, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await AuditedSoftDeleteAndBroadcastAsync(
            ctx,
            ctx.DeviceEvents.Where(e => e.DataSource == dataSource && e.SyncIdentifier == syncIdentifier),
            $"sync_identifier={dataSource}/{syncIdentifier}", DuplicateDelete.PromoteSurvivor, origin, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceEvent>> GetAllCopiesAsync(
        DateTime from, DateTime to, IReadOnlyCollection<DeviceEventType> eventTypes, CancellationToken ct = default)
    {
        var typeNames = eventTypes.Select(t => t.ToString()).ToList();
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.DeviceEvents.AsNoTracking()
            .Where(e => e.Timestamp >= from && e.Timestamp < to && typeNames.Contains(e.EventType))
            .OrderBy(e => e.Timestamp)
            .ToListAsync(ct);
        return entities.Select(DeviceEventMapper.ToDomainModel).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceEvent>> GetUserDeletedAsync(
        DateTime from, DateTime to, IReadOnlyCollection<DeviceEventType> eventTypes, CancellationToken ct = default)
    {
        var typeNames = eventTypes.Select(t => t.ToString()).ToList();
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.UserTombstones<DeviceEventEntity>()
            .Where(e => e.Timestamp >= from && e.Timestamp < to && typeNames.Contains(e.EventType))
            .OrderBy(e => e.Timestamp)
            .ToListAsync(ct);
        return entities.Select(DeviceEventMapper.ToDomainModel).ToList();
    }

    /// <inheritdoc />
    protected override DeviceEventEntity ToEntity(DeviceEvent model) => DeviceEventMapper.ToEntity(model);

    /// <inheritdoc />
    protected override DeviceEvent ToDomain(DeviceEventEntity entity) => DeviceEventMapper.ToDomainModel(entity);

    /// <inheritdoc />
    protected override void ApplyUpdate(DeviceEventEntity target, DeviceEvent source) => DeviceEventMapper.UpdateEntity(target, source);

    /// <summary>
    /// Gets device event records based on filter criteria.
    /// Hides the non-primary copies of duplicate groups.
    /// </summary>
    /// <param name="from">Optional start timestamp filter.</param>
    /// <param name="to">Optional end timestamp filter.</param>
    /// <param name="device">Optional device filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="offset">The number of records to skip.</param>
    /// <param name="descending">Whether to sort by timestamp in descending order.</param>
    /// <param name="patientDeviceId">Optional filter restricting results to events linked to a single registered patient device.</param>
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
        Guid? patientDeviceId = null,
        CancellationToken ct = default
    )
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await PagedRead(
            ctx.DeviceEvents.AsNoTracking(), from, to, device, source, limit, offset, descending,
            query => patientDeviceId.HasValue ? query.Where(e => e.PatientDeviceId == patientDeviceId.Value) : query)
            .ToListAsync(ct);
        return entities.Select(DeviceEventMapper.ToDomainModel);
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

        query = query.ExcludeDuplicates();

        var entity = await query
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : DeviceEventMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<DeviceEvent?> GetLatestOfAnyTypeAsync(
        IReadOnlyCollection<DeviceEventType> eventTypes, DateTime? asOf, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var eventTypeStrings = eventTypes.Select(t => t.ToString()).ToList();
        var query = ctx.DeviceEvents
            .AsNoTracking()
            .Where(e => eventTypeStrings.Contains(e.EventType));
        if (asOf is { } cutoff)
            query = query.Where(e => e.Timestamp <= cutoff);

        query = query.ExcludeDuplicates();

        var entity = await query
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : DeviceEventMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets the latest device event from a set of event types.
    /// </summary>
    /// <param name="eventTypes">The types of device events to search for.</param>
    /// <param name="patientDeviceId">Optional filter restricting the search to a single registered patient device.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The latest device event, or null if none found.</returns>
    public async Task<DeviceEvent?> GetLatestByEventTypesAsync(
        DeviceEventType[] eventTypes,
        Guid? patientDeviceId = null,
        CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var eventTypeStrings = eventTypes.Select(t => t.ToString()).ToList();
        var query = ctx.DeviceEvents
            .AsNoTracking()
            .Where(e => eventTypeStrings.Contains(e.EventType));

        if (patientDeviceId.HasValue)
            query = query.Where(e => e.PatientDeviceId == patientDeviceId.Value);

        var entity = await query
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : DeviceEventMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<int> CountByEventTypesSinceAsync(
        DeviceEventType[] eventTypes,
        DateTime since,
        CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var eventTypeStrings = eventTypes.Select(t => t.ToString()).ToList();
        return await ctx.DeviceEvents
            .AsNoTracking()
            .Where(e => eventTypeStrings.Contains(e.EventType) && e.Timestamp >= since)
            .CountAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceEvent>> GetUnattributedAsync(
        DateTime? from,
        DateTime? to,
        IReadOnlyCollection<DeviceEventType> eventTypes,
        int limit,
        CancellationToken ct = default)
    {
        if (eventTypes.Count == 0) return [];

        await using var ctx = await ContextFactory.CreateAsync(ct);
        var eventTypeStrings = eventTypes.Select(t => t.ToString()).ToList();
        var entities = await ctx.GetUnattributedAsync<DeviceEventEntity>(
            from, to, limit, ct, filter: e => eventTypeStrings.Contains(e.EventType));
        return entities.Select(DeviceEventMapper.ToDomainModel).ToList();
    }

    /// <inheritdoc />
    public async Task<int> SetPatientDeviceIdsAsync(IReadOnlyDictionary<Guid, Guid> patientDeviceIdByRecordId, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.SetPatientDeviceIdsAsync<DeviceEventEntity>(patientDeviceIdByRecordId, ct);
    }
}
