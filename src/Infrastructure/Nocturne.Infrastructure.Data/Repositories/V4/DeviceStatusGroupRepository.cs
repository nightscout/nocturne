using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <inheritdoc cref="IDeviceStatusGroupRepository"/>
public class DeviceStatusGroupRepository : IDeviceStatusGroupRepository
{
    private readonly INocturneDbContextFactory _contextFactory;
    private readonly ILogger<DeviceStatusGroupRepository> _logger;

    public DeviceStatusGroupRepository(
        INocturneDbContextFactory contextFactory, ILogger<DeviceStatusGroupRepository> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The arms are read one at a time in the page's order, each cut at <c>offset + limit</c> rows
    /// and bounded on the far side by the last row of the page so far: no row past it can enter the
    /// page, and an arm whose rows mostly belong to statuses an earlier arm anchors would otherwise
    /// be walked to its end through its anti-joins. Each arm is an index walk on its table's tenant
    /// timestamp index.
    /// </remarks>
    public async Task<IReadOnlyList<DeviceStatusGroup>> GetAsync(
        DateTime? from, DateTime? to, string? device, int limit, int offset, bool descending, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var rows = Rows.Live(ctx);
        var take = offset + limit;
        var page = new List<AnchorRow>();

        foreach (var arm in Arms(rows, from, to, device))
        {
            var bounded = arm;
            if (page.Count == take)
            {
                var edge = page[^1].Timestamp;
                bounded = descending
                    ? arm.Where(a => a.Timestamp >= edge)
                    : arm.Where(a => a.Timestamp <= edge);
            }

            var read = await (descending
                    ? bounded.OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id)
                    : bounded.OrderBy(a => a.Timestamp).ThenBy(a => a.Id))
                .Take(take)
                .ToListAsync(ct);
            page = Ordered(page.Concat(read), descending).Take(take).ToList();
        }

        var anchors = page.Skip(offset).ToList();
        var members = await Members.LoadAsync(rows, anchors, ct);
        return members.GroupsOf(anchors).Select(g => g.Group).ToList();
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(DateTime? from, DateTime? to, string? device, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await Arms(Rows.Live(ctx), from, to, device).Aggregate(Queryable.Concat).LongCountAsync(ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A status is stamped by the newest <c>sys_updated_at</c> of any of its rows, deleted ones
    /// included, so a change that moves its anchor to an older row (an APS removed from under a live
    /// pump) still reaches the client. The changed rows of each table are read in stamp order; every
    /// status whose stamp is below the earliest millisecond some table has not been read past is
    /// complete. The read widens until <paramref name="limit"/> statuses are, and the page ends on a
    /// millisecond boundary for the reason <see cref="HistoryPage"/> gives. Nothing at or after the
    /// <see cref="HistoryHorizon"/> is settled, whichever row of its status carries the stamp.
    /// </remarks>
    public async Task<IReadOnlyList<HistoryRecord<DeviceStatusGroup>>> GetModifiedSinceAsync(
        long cursorMills, int limit, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var rows = Rows.IncludingDeleted(ctx);
        var since = DateTimeOffset.FromUnixTimeMilliseconds(cursorMills + 1).UtcDateTime;
        var horizon = await HistoryHorizon.GetAsync(ctx, _logger, ct) ?? DateTime.MaxValue;

        // A status's rows are written together, so a quarter more rows than the page usually settles
        // it in one read; the doubling covers a batch that wrote one table before the next.
        for (var scan = limit + limit / 4 + 1; ; scan *= 2)
        {
            var changed = new[]
            {
                await ChangedAsync(rows.Aps, since, horizon, scan, ct),
                await ChangedAsync(rows.Pumps, since, horizon, scan, ct),
                await ChangedAsync(rows.Uploaders, since, horizon, scan, ct),
                await rows.Extras
                    .Where(e => e.SysUpdatedAt >= since && e.SysUpdatedAt < horizon)
                    .OrderBy(e => e.SysUpdatedAt)
                    .ThenBy(e => e.Id)
                    .Take(scan)
                    .Select(e => new ChangedRow { Id = e.Id, CorrelationId = e.CorrelationId, Stamp = e.SysUpdatedAt })
                    .ToListAsync(ct),
            };
            var readToTheEnd = changed.All(t => t.Count < scan);
            var settledBefore = changed
                .Where(t => t.Count == scan)
                .Select(t => Millisecond(t[^1].Stamp))
                .Append(horizon)
                .Min();

            var keys = changed.SelectMany(t => t).Where(r => r.Stamp < settledBefore).ToList();
            var correlationIds = keys
                .Where(r => r.CorrelationId.HasValue)
                .Select(r => r.CorrelationId!.Value)
                .Distinct()
                .ToArray();
            var ids = keys.Where(r => r.CorrelationId is null).Select(r => r.Id).ToArray();

            List<AnchorRow> anchors = keys.Count == 0
                ? []
                : await Arms(rows, from: null, to: null, device: null)
                    .Select(arm => arm.Where(a =>
                        (a.CorrelationId != null && correlationIds.Contains(a.CorrelationId.Value))
                        || ids.Contains(a.Id)))
                    .Aggregate(Queryable.Concat)
                    .ToListAsync(ct);
            var members = await Members.LoadAsync(rows, anchors, ct);
            var settled = members.GroupsOf(anchors)
                .Where(s => s.Group.ModifiedAt < settledBefore)
                .OrderBy(s => s.Group.ModifiedAt)
                .ThenBy(s => s.Anchor.Id)
                .ToList();

            if (settled.Count >= limit || readToTheEnd)
            {
                return EndOnAMillisecond(settled, limit)
                    .Select(s => new HistoryRecord<DeviceStatusGroup>(s.Group, s.Anchor.Deleted))
                    .ToList();
            }
        }
    }

    private static Task<List<ChangedRow>> ChangedAsync<T>(
        IQueryable<T> rows, DateTime since, DateTime horizon, int scan, CancellationToken ct)
        where T : class, IV4Entity, ISystemTimestamped =>
        rows.Where(e => e.SysUpdatedAt >= since && e.SysUpdatedAt < horizon)
            .OrderBy(e => e.SysUpdatedAt)
            .ThenBy(e => e.Id)
            .Take(scan)
            .Select(e => new ChangedRow { Id = e.Id, CorrelationId = e.CorrelationId, Stamp = e.SysUpdatedAt })
            .ToListAsync(ct);

    private static IEnumerable<(AnchorRow Anchor, DeviceStatusGroup Group)> EndOnAMillisecond(
        List<(AnchorRow Anchor, DeviceStatusGroup Group)> settled, int limit)
    {
        if (settled.Count <= limit)
            return settled;

        var last = settled[limit - 1].Group.ModifiedAt.ToMills();
        return settled.TakeWhile((s, i) => i < limit || s.Group.ModifiedAt.ToMills() == last);
    }

    private static DateTime Millisecond(DateTime stamp) =>
        DateTimeOffset.FromUnixTimeMilliseconds(stamp.ToMills()).UtcDateTime;

    private static IEnumerable<AnchorRow> Ordered(IEnumerable<AnchorRow> anchors, bool descending) =>
        descending
            ? anchors.OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id)
            : anchors.OrderBy(a => a.Timestamp).ThenBy(a => a.Id);

    /// <summary>
    /// The rows that place each status, one arm per table in the order APS, pump, uploader, extras.
    /// A row anchors its status unless a row of another table in the same status outranks it: a live
    /// row outranks a deleted one, and between two live or two deleted rows the first of APS, pump,
    /// uploader and extras wins. So a status whose APS was removed by a replace is anchored by its
    /// live pump, and a status reads as deleted only once every row of it is. Statuses with several
    /// APS rows keep one status per APS row.
    /// </summary>
    /// <remarks>
    /// Extras carry no device, so a device filter leaves out every extras-only status.
    /// </remarks>
    private static List<IQueryable<AnchorRow>> Arms(Rows rows, DateTime? from, DateTime? to, string? device)
    {
        var (aps, pumps, uploaders, extras) = rows;

        var arms = new List<IQueryable<AnchorRow>>
        {
            ApsSnapshotRepository.InWindow(aps, from, to, device)
                .Where(a => a.DeletedAt == null
                    || a.CorrelationId == null
                    || (!pumps.Any(p => p.CorrelationId == a.CorrelationId!.Value && p.DeletedAt == null)
                        && !uploaders.Any(u => u.CorrelationId == a.CorrelationId!.Value && u.DeletedAt == null)
                        && !extras.Any(e => e.CorrelationId == a.CorrelationId!.Value && e.DeletedAt == null)))
                .Select(a => new AnchorRow
                {
                    Kind = AnchorKind.Aps,
                    Id = a.Id,
                    CorrelationId = a.CorrelationId,
                    Timestamp = a.Timestamp,
                    Deleted = a.DeletedAt != null,
                }),
            PumpSnapshotRepository.InWindow(pumps, from, to, device)
                .Where(p => p.CorrelationId == null
                    || (!aps.Any(a => a.CorrelationId == p.CorrelationId!.Value && (a.DeletedAt == null || p.DeletedAt != null))
                        && (p.DeletedAt == null
                            || (!uploaders.Any(u => u.CorrelationId == p.CorrelationId!.Value && u.DeletedAt == null)
                                && !extras.Any(e => e.CorrelationId == p.CorrelationId!.Value && e.DeletedAt == null)))))
                .Select(p => new AnchorRow
                {
                    Kind = AnchorKind.Pump,
                    Id = p.Id,
                    CorrelationId = p.CorrelationId,
                    Timestamp = p.Timestamp,
                    Deleted = p.DeletedAt != null,
                }),
            UploaderSnapshotRepository.InWindow(uploaders, from, to, device)
                .Where(u => u.CorrelationId == null
                    || (!aps.Any(a => a.CorrelationId == u.CorrelationId!.Value && (a.DeletedAt == null || u.DeletedAt != null))
                        && !pumps.Any(p => p.CorrelationId == u.CorrelationId!.Value && (p.DeletedAt == null || u.DeletedAt != null))
                        && (u.DeletedAt == null
                            || !extras.Any(e => e.CorrelationId == u.CorrelationId!.Value && e.DeletedAt == null))))
                .Select(u => new AnchorRow
                {
                    Kind = AnchorKind.Uploader,
                    Id = u.Id,
                    CorrelationId = u.CorrelationId,
                    Timestamp = u.Timestamp,
                    Deleted = u.DeletedAt != null,
                }),
        };

        if (device != null)
            return arms;

        var extrasInWindow = extras;
        if (from.HasValue) extrasInWindow = extrasInWindow.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue) extrasInWindow = extrasInWindow.Where(e => e.Timestamp <= to.Value);

        arms.Add(extrasInWindow
            .Where(e => !aps.Any(a => a.CorrelationId == e.CorrelationId && (a.DeletedAt == null || e.DeletedAt != null))
                && !pumps.Any(p => p.CorrelationId == e.CorrelationId && (p.DeletedAt == null || e.DeletedAt != null))
                && !uploaders.Any(u => u.CorrelationId == e.CorrelationId && (u.DeletedAt == null || e.DeletedAt != null)))
            .Select(e => new AnchorRow
            {
                Kind = AnchorKind.Extras,
                Id = e.Id,
                CorrelationId = e.CorrelationId,
                Timestamp = e.Timestamp,
                Deleted = e.DeletedAt != null,
            }));
        return arms;
    }

    internal enum AnchorKind
    {
        Aps,
        Pump,
        Uploader,
        Extras,
    }

    internal sealed class AnchorRow
    {
        public AnchorKind Kind { get; init; }
        public Guid Id { get; init; }
        public Guid? CorrelationId { get; init; }
        public DateTime Timestamp { get; init; }
        public bool Deleted { get; init; }
    }

    private sealed class ChangedRow
    {
        public Guid Id { get; init; }
        public Guid? CorrelationId { get; init; }
        public DateTime Stamp { get; init; }
    }

    internal sealed record Rows(
        IQueryable<ApsSnapshotEntity> Aps,
        IQueryable<PumpSnapshotEntity> Pumps,
        IQueryable<UploaderSnapshotEntity> Uploaders,
        IQueryable<DeviceStatusExtrasEntity> Extras)
    {
        public static Rows Live(NocturneDbContext ctx) => new(
            ctx.ApsSnapshots.AsNoTracking(),
            ctx.PumpSnapshots.AsNoTracking(),
            ctx.UploaderSnapshots.AsNoTracking(),
            ctx.DeviceStatusExtras.AsNoTracking());

        public static Rows IncludingDeleted(NocturneDbContext ctx) => new(
            ctx.ApsSnapshots.IncludingDeleted().AsNoTracking(),
            ctx.PumpSnapshots.IncludingDeleted().AsNoTracking(),
            ctx.UploaderSnapshots.IncludingDeleted().AsNoTracking(),
            ctx.DeviceStatusExtras.IncludingDeleted().AsNoTracking());
    }

    /// <summary>
    /// Every row of a page's statuses, read in one query per table. A status takes the siblings as
    /// deleted as its anchor, so a live status never carries a row a replace removed, and is stamped
    /// by the newest of all its rows.
    /// </summary>
    internal sealed class Members
    {
        private readonly Table<ApsSnapshotEntity> _aps;
        private readonly Table<PumpSnapshotEntity> _pumps;
        private readonly Table<UploaderSnapshotEntity> _uploaders;
        private readonly Table<DeviceStatusExtrasEntity> _extras;

        internal Members(
            List<ApsSnapshotEntity> aps,
            List<PumpSnapshotEntity> pumps,
            List<UploaderSnapshotEntity> uploaders,
            List<DeviceStatusExtrasEntity> extras)
        {
            _aps = new(AnchorKind.Aps, aps, e => e.CorrelationId);
            _pumps = new(AnchorKind.Pump, pumps, e => e.CorrelationId);
            _uploaders = new(AnchorKind.Uploader, uploaders, e => e.CorrelationId);
            _extras = new(AnchorKind.Extras, extras, e => e.CorrelationId);
        }

        public static async Task<Members> LoadAsync(Rows rows, IReadOnlyList<AnchorRow> anchors, CancellationToken ct)
        {
            var correlationIds = anchors
                .Where(a => a.CorrelationId.HasValue)
                .Select(a => a.CorrelationId!.Value)
                .Distinct()
                .ToArray();

            return new Members(
                await LoadAsync(rows.Aps, AnchorIds(anchors, AnchorKind.Aps), correlationIds, ct),
                await LoadAsync(rows.Pumps, AnchorIds(anchors, AnchorKind.Pump), correlationIds, ct),
                await LoadAsync(rows.Uploaders, AnchorIds(anchors, AnchorKind.Uploader), correlationIds, ct),
                await LoadExtrasAsync(rows.Extras, AnchorIds(anchors, AnchorKind.Extras), correlationIds, ct));
        }

        /// <summary>
        /// The status of each anchor, in order. An anchor whose row is gone, deleted between the
        /// anchor read and this one, is left out.
        /// </summary>
        internal IEnumerable<(AnchorRow Anchor, DeviceStatusGroup Group)> GroupsOf(IEnumerable<AnchorRow> anchors)
        {
            foreach (var anchor in anchors)
            {
                var aps = _aps.Member(anchor);
                var pump = _pumps.Member(anchor);
                var uploader = _uploaders.Member(anchor);
                var extras = _extras.Member(anchor);
                if (anchor.Kind switch
                    {
                        AnchorKind.Aps => aps,
                        AnchorKind.Pump => pump,
                        AnchorKind.Uploader => uploader,
                        _ => (object?)extras,
                    } is null)
                    continue;

                var stamp = new[] { _aps.Stamp(anchor), _pumps.Stamp(anchor), _uploaders.Stamp(anchor), _extras.Stamp(anchor) }
                    .Max()!.Value;

                yield return (anchor, new DeviceStatusGroup(
                    aps is null ? null : ApsSnapshotMapper.ToDomainModel(aps),
                    pump is null ? null : PumpSnapshotMapper.ToDomainModel(pump),
                    uploader is null ? null : UploaderSnapshotMapper.ToDomainModel(uploader),
                    extras is null ? null : DeviceStatusExtrasMapper.ToDomainModel(extras),
                    stamp));
            }
        }

        private static Guid[] AnchorIds(IEnumerable<AnchorRow> anchors, AnchorKind kind) =>
            anchors.Where(a => a.Kind == kind).Select(a => a.Id).ToArray();

        private static async Task<List<T>> LoadAsync<T>(
            IQueryable<T> rows, Guid[] ids, Guid[] correlationIds, CancellationToken ct)
            where T : class, IV4Entity
        {
            if (ids.Length == 0 && correlationIds.Length == 0)
                return [];

            return await rows
                .Where(e => ids.Contains(e.Id)
                    || (e.CorrelationId != null && correlationIds.Contains(e.CorrelationId.Value)))
                .OrderBy(e => e.Id)
                .ToListAsync(ct);
        }

        /// <summary>One table's rows of the page, keyed by row and by status.</summary>
        private sealed class Table<T>
            where T : class, IIdentified, ISoftDeletable, ISystemTimestamped
        {
            private readonly AnchorKind _kind;
            private readonly Dictionary<Guid, T> _byId;
            private readonly ILookup<Guid, T> _byStatus;

            public Table(AnchorKind kind, List<T> rows, Func<T, Guid?> correlationId)
            {
                _kind = kind;
                _byId = rows.ToDictionary(r => r.Id);
                _byStatus = rows.ToLookup(r => correlationId(r) ?? r.Id);
            }

            public T? Member(AnchorRow anchor)
            {
                if (anchor.Kind == _kind)
                    return _byId.GetValueOrDefault(anchor.Id);

                return anchor.CorrelationId is { } id
                    ? _byStatus[id].FirstOrDefault(r => (r.DeletedAt != null) == anchor.Deleted)
                    : null;
            }

            public DateTime? Stamp(AnchorRow anchor) =>
                _byStatus[anchor.CorrelationId ?? anchor.Id].Select(r => (DateTime?)r.SysUpdatedAt).Max();
        }

        private static async Task<List<DeviceStatusExtrasEntity>> LoadExtrasAsync(
            IQueryable<DeviceStatusExtrasEntity> rows, Guid[] ids, Guid[] correlationIds, CancellationToken ct)
        {
            if (ids.Length == 0 && correlationIds.Length == 0)
                return [];

            return await rows
                .Where(e => ids.Contains(e.Id) || correlationIds.Contains(e.CorrelationId))
                .OrderBy(e => e.Id)
                .ToListAsync(ct);
        }
    }
}
