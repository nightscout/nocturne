using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// What a soft delete does to the other copies in the duplicate groups of the rows it deletes.
/// </summary>
public enum DuplicateDelete
{
    /// <summary>
    /// A source going away (a connector or data source removed, a source's time range swept): the
    /// other sources still reported the record, so a group whose primary went is repointed onto a
    /// live copy (<see cref="DuplicateGroupPrimaries.RepointAwayFromAsync"/>).
    /// </summary>
    PromoteSurvivor,

    /// <summary>
    /// A user deleting one record: every copy goes with it, so a bolus reported by both the pump
    /// and the AID app stops counting in IOB/COB.
    /// </summary>
    EveryCopy,
}

/// <summary>
/// Keeps a duplicate group readable once its primary is soft-deleted. Reads show a group through
/// its primary (<see cref="ReadVisibilityFilter.ExcludeDuplicates{TEntity}"/>), so a deleted
/// primary hides every other source's copy with it; and the promoted copy's own row did not
/// change, so a v3 history client would never be sent it. Every primary move, here or in
/// <see cref="DeduplicationService"/>, is written by <see cref="SetPrimariesAsync"/>.
/// </summary>
/// <remarks>
/// The repoint writes by statement and tracks nothing, so the context may be the caller's scope
/// context with changes of its own pending.
/// </remarks>
internal static class DuplicateGroupPrimaries
{
    private static readonly Dictionary<Type, RecordType> RecordTypes = new()
    {
        [typeof(SensorGlucoseEntity)] = RecordType.SensorGlucose,
        [typeof(BolusEntity)] = RecordType.Bolus,
        [typeof(CarbIntakeEntity)] = RecordType.CarbIntake,
        [typeof(BGCheckEntity)] = RecordType.BGCheck,
        [typeof(DeviceEventEntity)] = RecordType.DeviceEvent,
        [typeof(NoteEntity)] = RecordType.Note,
        [typeof(BolusCalculationEntity)] = RecordType.BolusCalculation,
        [typeof(TempBasalEntity)] = RecordType.TempBasal,
        [typeof(StateSpanEntity)] = RecordType.StateSpan,
    };

    /// <summary>The record type rows of <typeparamref name="TEntity"/> are linked under, or null.</summary>
    public static RecordType? RecordTypeOf<TEntity>() => RecordTypeOf(typeof(TEntity));

    /// <inheritdoc cref="RecordTypeOf{TEntity}"/>
    public static RecordType? RecordTypeOf(Type entityType) =>
        RecordTypes.TryGetValue(entityType, out var recordType) ? recordType : null;

    /// <summary>
    /// Moves the primary of every group one of <paramref name="deletedIds"/> is the primary of onto
    /// the group's survivor, and stamps each promoted live copy's <c>SysUpdatedAt</c> from
    /// <paramref name="touchAt"/>, <see cref="NocturneDbContext.SystemTimestampGroupSize"/> rows to a
    /// millisecond. Groups the deleted rows were only a duplicate in are left as they are.
    /// </summary>
    /// <returns>The ids of the promoted live copies.</returns>
    public static async Task<IReadOnlyList<Guid>> RepointAwayFromAsync(
        NocturneDbContext ctx,
        RecordType recordType,
        IReadOnlyCollection<Guid> deletedIds,
        DateTime touchAt,
        CancellationToken ct)
    {
        if (deletedIds.Count == 0)
            return [];

        var key = RecordTypeKeys.Key(recordType);
        var ids = deletedIds.ToArray();
        var canonicals = await ctx.LinkedRecords.AsNoTracking()
            .Where(lr => lr.RecordType == key && lr.IsPrimary && ids.Contains(lr.RecordId))
            .Select(lr => lr.CanonicalId)
            .Distinct()
            .ToArrayAsync(ct);
        if (canonicals.Length == 0)
            return [];

        var promoted = await RepickAsync(ctx, recordType, canonicals, ct);
        await TouchAsync(ctx, recordType, promoted, touchAt, ct);
        return promoted;
    }

    /// <summary>
    /// Gives every group one of <paramref name="restoredIds"/> is linked into, and whose primary is
    /// not live, a live primary, and stamps each promoted copy's <c>SysUpdatedAt</c> from
    /// <paramref name="touchAt"/>. A group whose primary is live keeps it.
    /// </summary>
    /// <returns>The ids of the live records made primary.</returns>
    public static async Task<IReadOnlyList<Guid>> RepointHeadlessGroupsAsync(
        NocturneDbContext ctx,
        RecordType recordType,
        IReadOnlyCollection<Guid> restoredIds,
        DateTime touchAt,
        CancellationToken ct)
    {
        if (restoredIds.Count == 0)
            return [];

        var key = RecordTypeKeys.Key(recordType);
        var ids = restoredIds.ToArray();
        var canonicals = await ctx.LinkedRecords.AsNoTracking()
            .Where(lr => lr.RecordType == key && ids.Contains(lr.RecordId))
            .Select(lr => lr.CanonicalId)
            .Distinct()
            .ToArrayAsync(ct);
        if (canonicals.Length == 0)
            return [];

        var primaries = await ctx.LinkedRecords.AsNoTracking()
            .Where(lr => lr.RecordType == key && lr.IsPrimary && canonicals.Contains(lr.CanonicalId))
            .Select(lr => new { lr.CanonicalId, lr.RecordId })
            .ToListAsync(ct);
        var live = await LiveIdsAsync(ctx, recordType, primaries.Select(p => p.RecordId).Distinct().ToArray(), ct);
        var headed = primaries.Where(p => live.Contains(p.RecordId)).Select(p => p.CanonicalId).ToHashSet();
        var headless = canonicals.Where(c => !headed.Contains(c)).ToArray();
        if (headless.Length == 0)
            return [];

        var promoted = await RepickAsync(ctx, recordType, headless, ct);
        await TouchAsync(ctx, recordType, promoted, touchAt, ct);
        return promoted;
    }

    /// <summary>
    /// Soft-deletes <paramref name="entity"/>, a tracked row of <paramref name="ctx"/>, together with
    /// every other copy in its duplicate group, in one transaction, so a failure leaves them all live.
    /// This is a user deleting one record: the dose or reading is gone whichever source reported it,
    /// so no other copy may be promoted to keep counting (<see cref="DuplicateDelete"/>).
    /// </summary>
    /// <param name="ctx">The context tracking <paramref name="entity"/>.</param>
    /// <param name="entity">The row to delete.</param>
    /// <param name="recordType">The record type its links are under; null deletes the row alone.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>What the save wrote, and the other copies deleted with the row.</returns>
    public static Task<(int Saved, IReadOnlyList<TEntity> Copies)> SoftDeleteAsync<TEntity>(
        NocturneDbContext ctx, TEntity entity, RecordType? recordType, CancellationToken ct)
        where TEntity : class, IIdentified, ISoftDeletable =>
        ctx.ExecuteInTransactionAsync<(int, IReadOnlyList<TEntity>)>(async token =>
        {
            var deletedAt = NocturneDbContext.UtcNowAtStoredPrecision();
            entity.DeletedAt = deletedAt;
            List<TEntity> copies = [];
            if (recordType is { } type)
            {
                var copyIds = await GroupMatesAsync(ctx, type, [entity.Id], token);
                if (copyIds.Length > 0)
                    copies = await ctx.Set<TEntity>().Where(e => copyIds.Contains(e.Id)).ToListAsync(token);
                foreach (var copy in copies)
                    copy.DeletedAt = deletedAt;
            }

            var saved = await ctx.SaveChangesAsync(token);
            if (recordType == RecordType.SensorGlucose)
                await GlucoseStreamFlags.AfterRowsChangedAsync(ctx, [entity.Id, .. copies.Select(c => c.Id)], token);
            return (saved, copies);
        }, ct: ct);

    /// <summary>
    /// <paramref name="query"/> widened to every live copy in the duplicate groups of the rows it
    /// matches, for a bulk delete made with <see cref="DuplicateDelete.EveryCopy"/>. Unchanged when
    /// <typeparamref name="T"/> takes no part in deduplication or no matched row is linked.
    /// </summary>
    public static async Task<IQueryable<T>> WithGroupMatesAsync<T>(
        NocturneDbContext ctx, IQueryable<T> query, CancellationToken ct)
        where T : class, ISoftDeletable
    {
        if (RecordTypeOf<T>() is not { } recordType)
            return query;

        var ids = await query.Where(e => e.DeletedAt == null)
            .Select(e => EF.Property<Guid>(e, "Id")).ToArrayAsync(ct);
        var mates = await GroupMatesAsync(ctx, recordType, ids, ct);
        if (mates.Length == 0)
            return query;

        Guid[] all = [.. ids, .. mates];
        return ctx.Set<T>().Where(e => all.Contains(EF.Property<Guid>(e, "Id")));
    }

    /// <summary>The other records linked into the duplicate groups of <paramref name="ids"/>.</summary>
    private static async Task<Guid[]> GroupMatesAsync(
        NocturneDbContext ctx, RecordType recordType, Guid[] ids, CancellationToken ct)
    {
        if (ids.Length == 0)
            return [];

        var key = RecordTypeKeys.Key(recordType);
        var canonicals = await ctx.LinkedRecords.AsNoTracking()
            .Where(lr => lr.RecordType == key && ids.Contains(lr.RecordId))
            .Select(lr => lr.CanonicalId)
            .Distinct()
            .ToArrayAsync(ct);
        if (canonicals.Length == 0)
            return [];

        return await ctx.LinkedRecords.AsNoTracking()
            .Where(lr => lr.RecordType == key && canonicals.Contains(lr.CanonicalId) && !ids.Contains(lr.RecordId))
            .Select(lr => lr.RecordId)
            .Distinct()
            .ToArrayAsync(ct);
    }

    /// <summary>
    /// Canonical groups <see cref="RepickAsync"/> loads the links of at a time, so a delete of a
    /// whole source holds one chunk's links in memory rather than every group's.
    /// </summary>
    internal const int RepickChunkSize = 1000;

    /// <summary>
    /// Moves each group's <see cref="LinkedRecordEntity.IsPrimary"/> onto
    /// <see cref="DeduplicationService.PickSurvivor(IEnumerable{LinkedRecordEntity}, Func{Guid, bool}, Func{Guid, DeduplicationService.SpanEnd})"/>,
    /// <see cref="RepickChunkSize"/> groups at a time. A group with no primary at all renders as
    /// nothing, so it is given one here too.
    /// </summary>
    /// <returns>The ids of the live records made primary.</returns>
    public static async Task<IReadOnlyList<Guid>> RepickAsync(
        NocturneDbContext ctx, RecordType recordType, Guid[] canonicalIds, CancellationToken ct)
    {
        var promotedLive = new List<Guid>();
        foreach (var chunk in canonicalIds.Chunk(RepickChunkSize))
            promotedLive.AddRange(await RepickChunkAsync(ctx, recordType, chunk, ct));
        return promotedLive;
    }

    private static async Task<List<Guid>> RepickChunkAsync(
        NocturneDbContext ctx, RecordType recordType, Guid[] canonicalIds, CancellationToken ct)
    {
        var key = RecordTypeKeys.Key(recordType);
        var rows = await ctx.LinkedRecords.AsNoTracking()
            .Where(lr => lr.RecordType == key && canonicalIds.Contains(lr.CanonicalId))
            .ToListAsync(ct);
        var ids = rows.Select(r => r.RecordId).Distinct().ToArray();
        var spanEnds = recordType == RecordType.StateSpan ? await LiveSpanEndsAsync(ctx, ids, ct) : [];
        var live = recordType == RecordType.StateSpan
            ? spanEnds.Keys.ToHashSet()
            : await LiveIdsAsync(ctx, recordType, ids, ct);

        var changes = new List<PrimaryChange>();
        var promotedLive = new List<Guid>();
        foreach (var group in rows.GroupBy(r => r.CanonicalId))
        {
            var survivor = DeduplicationService.PickSurvivor(group, live.Contains, spanEnds.GetValueOrDefault);
            if (survivor.IsPrimary)
                continue;

            changes.AddRange(group.Where(r => r.IsPrimary).Select(r => new PrimaryChange(r.RecordId, false)));
            changes.Add(new PrimaryChange(survivor.RecordId, true));
            if (live.Contains(survivor.RecordId))
                promotedLive.Add(survivor.RecordId);
        }

        await SetPrimariesAsync(ctx, recordType, changes, ct);
        return promotedLive;
    }

    /// <summary>Ids <see cref="SetPrimariesAsync"/> writes per statement.</summary>
    internal const int SetPrimariesChunkSize = 10_000;

    /// <summary>
    /// Makes each record's link its group's primary or not, as <paramref name="changes"/> says, and
    /// sets each record's <see cref="IDuplicateFlagged.IsDuplicate"/> from its link as it then stands,
    /// in the caller's transaction. Every change to <see cref="LinkedRecordEntity.IsPrimary"/> of a
    /// stored link goes through here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The chunk's links are locked first, in record order, whether or not their primary changes, so
    /// no other transaction can move them until this one commits. The flag is then derived from the
    /// links the statement sees rather than from <paramref name="changes"/>, so a caller whose plan
    /// was overtaken by a concurrent writer still leaves the flag equal to the committed link.
    /// SQLite takes no row locks; its write transactions are serialised on the whole database.
    /// </para>
    /// <para>
    /// One statement per direction over an array of ids, and only rows that differ are written, so
    /// an unchanged row gains no dead tuple. EF statements rather than a <c>VALUES</c> join keep one
    /// path for PostgreSQL and the SQLite unit tests.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">No transaction is open on a relational context.</exception>
    public static async Task SetPrimariesAsync(
        NocturneDbContext ctx, RecordType recordType, IReadOnlyCollection<PrimaryChange> changes, CancellationToken ct)
    {
        RequireTransaction(ctx);
        await LockStreamsAsync(ctx, recordType, ct);
        var key = RecordTypeKeys.Key(recordType);
        foreach (var chunk in changes.OrderBy(c => c.RecordId).Chunk(SetPrimariesChunkSize))
        {
            var ids = chunk.Select(c => c.RecordId).ToArray();
            var promote = chunk.Where(c => c.IsPrimary).Select(c => c.RecordId).ToArray();
            var demote = chunk.Where(c => !c.IsPrimary).Select(c => c.RecordId).ToArray();

            await LockLinksAsync(ctx, key, ids, ct);

            if (promote.Length > 0)
                await ctx.LinkedRecords
                    .Where(lr => lr.RecordType == key && promote.Contains(lr.RecordId) && !lr.IsPrimary)
                    .ExecuteUpdateAsync(s => s.SetProperty(lr => lr.IsPrimary, true), ct);
            if (demote.Length > 0)
                await ctx.LinkedRecords
                    .Where(lr => lr.RecordType == key && demote.Contains(lr.RecordId) && lr.IsPrimary)
                    .ExecuteUpdateAsync(s => s.SetProperty(lr => lr.IsPrimary, false), ct);

            await DeriveAndRecomputeStreamsAsync(ctx, recordType, ids, ct);
        }
    }

    /// <summary>
    /// Sets the flag of the records whose links this transaction has just inserted. A link not yet
    /// committed is invisible to every other transaction, so it needs no lock and no primary change
    /// (<see cref="SetPrimariesAsync"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open on a relational context.</exception>
    public static async Task FlagInsertedLinksAsync(
        NocturneDbContext ctx, RecordType recordType, IReadOnlyCollection<Guid> recordIds, CancellationToken ct)
    {
        RequireTransaction(ctx);
        await LockStreamsAsync(ctx, recordType, ct);
        foreach (var chunk in recordIds.Order().Chunk(SetPrimariesChunkSize))
            await DeriveAndRecomputeStreamsAsync(ctx, recordType, chunk, ct);
    }

    /// <summary>
    /// Sets the flag of each of <paramref name="recordIds"/> that disagrees with its link. The links
    /// are locked first, as <see cref="SetPrimariesAsync"/> locks them, so a repair and a primary move
    /// wait for each other and whichever runs second derives from what the first committed.
    /// </summary>
    /// <remarks>
    /// The caller also holds the tenant's dedup write lock (<c>DeduplicationService.UnderWriteLockAsync</c>),
    /// under which every link is inserted. Without it a repair could clear a flag whose non-primary
    /// link another transaction is inserting: that transaction finds the flag already set and writes
    /// nothing, and the repair does not see the uncommitted link. Locks are taken in the order
    /// <see cref="GlucoseStreamFlags"/> states: dedup write lock, then for glucose the stream lock
    /// (<see cref="GlucoseStreamFlags.LockAsync"/>), then the links. A glucose row the repair shows or
    /// hides has its bucket's stream flags recomputed in the same transaction.
    /// </remarks>
    /// <returns>The rows whose flag was rewritten.</returns>
    /// <exception cref="InvalidOperationException">No transaction is open on a relational context.</exception>
    public static async Task<int> RepairFlagsAsync(
        NocturneDbContext ctx, RecordType recordType, IReadOnlyCollection<Guid> recordIds, CancellationToken ct)
    {
        RequireTransaction(ctx);
        await LockStreamsAsync(ctx, recordType, ct);
        var key = RecordTypeKeys.Key(recordType);
        var repaired = 0;
        foreach (var chunk in recordIds.Order().Chunk(SetPrimariesChunkSize))
        {
            await LockLinksAsync(ctx, key, chunk, ct);
            repaired += await DeriveAndRecomputeStreamsAsync(ctx, recordType, chunk, ct);
        }

        return repaired;
    }

    /// <summary>
    /// Locks the links of <paramref name="recordIds"/> until the transaction ends, in record order, so
    /// two lockers of overlapping sets queue rather than deadlock. SQLite takes no row locks; its write
    /// transactions are serialised on the whole database.
    /// </summary>
    private static async Task LockLinksAsync(NocturneDbContext ctx, string key, Guid[] recordIds, CancellationToken ct)
    {
        if (!ctx.Database.IsNpgsql())
            return;

        await ctx.Database.ExecuteSqlAsync($"""
            SELECT id FROM linked_records
            WHERE tenant_id = {ctx.TenantId} AND record_type = {key} AND record_id = ANY ({recordIds})
            ORDER BY record_id
            FOR UPDATE
            """, ct);
    }

    /// <summary>
    /// <see cref="DeriveFlagsAsync"/>, then, for glucose readings whose flag moved, their buckets' stream
    /// flags: a duplicate hidden or shown changes which streams a bucket holds.
    /// </summary>
    /// <returns>The rows whose duplicate flag was rewritten.</returns>
    private static async Task<int> DeriveAndRecomputeStreamsAsync(
        NocturneDbContext ctx, RecordType recordType, Guid[] ids, CancellationToken ct)
    {
        var derived = await DeriveFlagsAsync(ctx, recordType, ids, ct);
        if (derived > 0 && recordType == RecordType.SensorGlucose)
            await GlucoseStreamFlags.AfterRowsChangedAsync(ctx, ids, ct);
        return derived;
    }

    /// <summary>The stream lock ahead of a glucose link or flag write (<see cref="GlucoseStreamFlags.LockAsync"/>).</summary>
    private static Task LockStreamsAsync(NocturneDbContext ctx, RecordType recordType, CancellationToken ct) =>
        recordType == RecordType.SensorGlucose ? GlucoseStreamFlags.LockAsync(ctx, ct) : Task.CompletedTask;

    private static void RequireTransaction(NocturneDbContext ctx)
    {
        if (ctx.Database.IsRelational() && ctx.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "Primaries change in the transaction of the link change they belong to; none is open.");
    }

    /// <summary>
    /// Sets each of <paramref name="ids"/>' flag to whether it has a non-primary link, with the history
    /// clamp lifted (<see cref="HistoryClampLiftExtensions"/>): a clamped caller's link change reaches
    /// records older than its window, which the clamp would silently leave stale. The statement sets
    /// only the flag and returns a count.
    /// </summary>
    private static Task<int> DeriveFlagsAsync(
        NocturneDbContext ctx, RecordType recordType, Guid[] ids, CancellationToken ct) =>
        ids.Length == 0 ? Task.FromResult(0) : ctx.WriteWithHistoryClampLiftedAsync(token => recordType switch
        {
            RecordType.SensorGlucose => Derive(ctx, ctx.SensorGlucose, recordType, ids, token),
            RecordType.Bolus => Derive(ctx, ctx.Boluses, recordType, ids, token),
            RecordType.CarbIntake => Derive(ctx, ctx.CarbIntakes, recordType, ids, token),
            RecordType.BGCheck => Derive(ctx, ctx.BGChecks, recordType, ids, token),
            RecordType.DeviceEvent => Derive(ctx, ctx.DeviceEvents, recordType, ids, token),
            RecordType.Note => Derive(ctx, ctx.Notes, recordType, ids, token),
            RecordType.BolusCalculation => Derive(ctx, ctx.BolusCalculations, recordType, ids, token),
            RecordType.TempBasal => Derive(ctx, ctx.TempBasals, recordType, ids, token),
            RecordType.StateSpan => Derive(ctx, ctx.StateSpans, recordType, ids, token),
            _ => throw new ArgumentOutOfRangeException(nameof(recordType), recordType, null),
        }, ct);

    /// <remarks>
    /// Soft-deleted rows keep their links, so they keep their flag too. The statement writes only rows
    /// that disagree with their link, so negating the flag sets it to the link.
    /// </remarks>
    private static Task<int> Derive<TEntity>(
        NocturneDbContext ctx, DbSet<TEntity> rows, RecordType recordType, Guid[] ids, CancellationToken ct)
        where TEntity : class, IDuplicateFlagged =>
        Disagreeing(ctx, rows.IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey]), recordType, ids)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsDuplicate, e => !e.IsDuplicate), ct);

    /// <summary>
    /// The rows of <paramref name="rows"/> among <paramref name="ids"/> whose
    /// <see cref="IDuplicateFlagged.IsDuplicate"/> is not whether a non-primary link names them: the
    /// flag's rule, stated once.
    /// </summary>
    /// <remarks>
    /// The links are bounded by <paramref name="ids"/> as well as by the row. The planner may run the
    /// <c>EXISTS</c> as one hashed lookup, and unbounded that reads every non-primary link of the
    /// tenant for each statement.
    /// </remarks>
    private static IQueryable<TEntity> Disagreeing<TEntity>(
        NocturneDbContext ctx, IQueryable<TEntity> rows, RecordType recordType, Guid[] ids)
        where TEntity : class, IDuplicateFlagged
    {
        var key = RecordTypeKeys.Key(recordType);
        return rows.Where(e => ids.Contains(e.Id)
            && e.IsDuplicate != ctx.LinkedRecords.Any(lr =>
                lr.RecordType == key && ids.Contains(lr.RecordId) && lr.RecordId == e.Id && !lr.IsPrimary));
    }

    /// <summary>Rows <see cref="DisagreeingPagesAsync"/> reads per page.</summary>
    internal const int AuditPageSize = 10_000;

    /// <summary>
    /// Every row of <paramref name="recordType"/>, soft-deleted included, whose flag disagrees with its
    /// link, read <paramref name="pageSize"/> rows at a time and yielded a page at a time when the page
    /// holds any. Each page is two statements in key order over a tenant-leading index, the page's keys
    /// and then its disagreeing rows, each read whole, so no reader is open while the caller repairs a
    /// page on the same context. A row whose key moves on during the walk is read again at its new
    /// place. <c>sys_updated_at</c> is stamped from the API clock before the save commits, so a row
    /// that commits after the walk has passed its stamp is missed by this walk and read by the next.
    /// </summary>
    /// <remarks>
    /// The eight tables with a <c>sys_updated_at</c> are walked on their non-partial
    /// <c>(tenant_id, sys_updated_at, id)</c> history index, and state spans, which have no such stamp,
    /// on their <c>(tenant_id, id)</c> primary key. The flag's partial read indexes cannot serve the
    /// walk: a wrongly flagged row is exactly one they leave out.
    /// </remarks>
    public static IAsyncEnumerable<DisagreeingRow[]> DisagreeingPagesAsync(
        NocturneDbContext ctx, RecordType recordType, int pageSize, CancellationToken ct) => recordType switch
    {
        RecordType.SensorGlucose => ByUpdate(ctx, ctx.SensorGlucose, recordType, pageSize, ct),
        RecordType.Bolus => ByUpdate(ctx, ctx.Boluses, recordType, pageSize, ct),
        RecordType.CarbIntake => ByUpdate(ctx, ctx.CarbIntakes, recordType, pageSize, ct),
        RecordType.BGCheck => ByUpdate(ctx, ctx.BGChecks, recordType, pageSize, ct),
        RecordType.DeviceEvent => ByUpdate(ctx, ctx.DeviceEvents, recordType, pageSize, ct),
        RecordType.Note => ByUpdate(ctx, ctx.Notes, recordType, pageSize, ct),
        RecordType.BolusCalculation => ByUpdate(ctx, ctx.BolusCalculations, recordType, pageSize, ct),
        RecordType.TempBasal => ByUpdate(ctx, ctx.TempBasals, recordType, pageSize, ct),
        RecordType.StateSpan => ById(ctx, ctx.StateSpans, recordType, pageSize, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(recordType), recordType, null),
    };

    private abstract record PageKey(Guid Id);

    private sealed record UpdateKey(DateTime At, Guid Id) : PageKey(Id);

    private sealed record IdKey(Guid Id) : PageKey(Id);

    private static IAsyncEnumerable<DisagreeingRow[]> ByUpdate<TEntity>(
        NocturneDbContext ctx, DbSet<TEntity> set, RecordType recordType, int pageSize, CancellationToken ct)
        where TEntity : class, IDuplicateFlagged, ISystemTimestamped
    {
        var rows = set.IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey]).AsNoTracking();
        return PagesAsync<TEntity, UpdateKey>(
            ctx, recordType, pageSize,
            after => (after is null
                    ? rows
                    // The redundant lower bound is what the planner ranges the index on; the
                    // disjunction alone is not recognised as a range.
                    : rows.Where(e => e.SysUpdatedAt >= after.At
                        && (e.SysUpdatedAt > after.At || e.Id.CompareTo(after.Id) > 0)))
                .OrderBy(e => e.SysUpdatedAt).ThenBy(e => e.Id),
            (page, last) => page.Where(e => e.SysUpdatedAt <= last.At
                && (e.SysUpdatedAt < last.At || e.Id.CompareTo(last.Id) <= 0)),
            e => new UpdateKey(e.SysUpdatedAt, e.Id),
            ct);
    }

    private static IAsyncEnumerable<DisagreeingRow[]> ById<TEntity>(
        NocturneDbContext ctx, DbSet<TEntity> set, RecordType recordType, int pageSize, CancellationToken ct)
        where TEntity : class, IDuplicateFlagged
    {
        var rows = set.IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey]).AsNoTracking();
        return PagesAsync<TEntity, IdKey>(
            ctx, recordType, pageSize,
            after => (after is null ? rows : rows.Where(e => e.Id.CompareTo(after.Id) > 0)).OrderBy(e => e.Id),
            (page, last) => page.Where(e => e.Id.CompareTo(last.Id) <= 0),
            e => new IdKey(e.Id),
            ct);
    }

    /// <summary>
    /// The walk of <see cref="DisagreeingPagesAsync"/> over one key: <c>after</c> gives the rows
    /// strictly after a key (every row for null) in key order, <c>upTo</c> narrows rows to those at or
    /// before a key, so a page's second statement ranges the same index entries as its first, and
    /// <c>keyOf</c> reads a row's key.
    /// </summary>
    private static async IAsyncEnumerable<DisagreeingRow[]> PagesAsync<TEntity, TKey>(
        NocturneDbContext ctx,
        RecordType recordType,
        int pageSize,
        Func<TKey?, IQueryable<TEntity>> after,
        Func<IQueryable<TEntity>, TKey, IQueryable<TEntity>> upTo,
        Expression<Func<TEntity, TKey>> keyOf,
        [EnumeratorCancellation] CancellationToken ct)
        where TEntity : class, IDuplicateFlagged
        where TKey : PageKey
    {
        TKey? cursor = null;
        while (true)
        {
            var keys = await after(cursor).Take(pageSize).Select(keyOf).ToListAsync(ct);
            if (keys.Count == 0)
                yield break;

            var last = keys[^1];
            var disagreeing = await Disagreeing(ctx, upTo(after(cursor), last), recordType, [.. keys.Select(k => k.Id)])
                .Select(e => new DisagreeingRow(e.Id, e.IsDuplicate))
                .ToArrayAsync(ct);
            if (disagreeing.Length > 0)
                yield return disagreeing;

            if (keys.Count < pageSize)
                yield break;
            cursor = last;
        }
    }

    private static Task<HashSet<Guid>> LiveIdsAsync(
        NocturneDbContext ctx, RecordType recordType, Guid[] ids, CancellationToken ct) => recordType switch
    {
        RecordType.SensorGlucose => Live(ctx.SensorGlucose, ids, ct),
        RecordType.Bolus => Live(ctx.Boluses, ids, ct),
        RecordType.CarbIntake => Live(ctx.CarbIntakes, ids, ct),
        RecordType.BGCheck => Live(ctx.BGChecks, ids, ct),
        RecordType.DeviceEvent => Live(ctx.DeviceEvents, ids, ct),
        RecordType.Note => Live(ctx.Notes, ids, ct),
        RecordType.BolusCalculation => Live(ctx.BolusCalculations, ids, ct),
        RecordType.TempBasal => Live(ctx.TempBasals, ids, ct),
        RecordType.StateSpan => Live(ctx.StateSpans, ids, ct),
        _ => Task.FromResult(new HashSet<Guid>()),
    };

    /// <summary>How each live state span among <paramref name="ids"/> ended, keyed by id.</summary>
    private static async Task<Dictionary<Guid, DeduplicationService.SpanEnd>> LiveSpanEndsAsync(
        NocturneDbContext ctx, Guid[] ids, CancellationToken ct) =>
        (await ctx.StateSpans.AsNoTracking()
            .Where(s => s.DeletedAt == null && ids.Contains(s.Id))
            .Select(s => new { s.Id, s.SupersededById, s.EndTimestamp })
            .ToListAsync(ct))
        .ToDictionary(s => s.Id, s => new DeduplicationService.SpanEnd(s.SupersededById, s.EndTimestamp));

    private static async Task<HashSet<Guid>> Live<TEntity>(IQueryable<TEntity> rows, Guid[] ids, CancellationToken ct)
        where TEntity : class, IIdentified, ISoftDeletable =>
        (await rows.AsNoTracking().Where(e => e.DeletedAt == null && ids.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct))
        .ToHashSet();

    /// <remarks>State spans carry no <c>SysUpdatedAt</c> and no v3 history, so they are not touched.</remarks>
    private static Task TouchAsync(
        NocturneDbContext ctx, RecordType recordType, IReadOnlyList<Guid> ids, DateTime touchAt, CancellationToken ct) =>
        ids.Count == 0 ? Task.CompletedTask : recordType switch
        {
            RecordType.SensorGlucose => Touch(ctx.SensorGlucose, ids, touchAt, ct),
            RecordType.Bolus => Touch(ctx.Boluses, ids, touchAt, ct),
            RecordType.CarbIntake => Touch(ctx.CarbIntakes, ids, touchAt, ct),
            RecordType.BGCheck => Touch(ctx.BGChecks, ids, touchAt, ct),
            RecordType.DeviceEvent => Touch(ctx.DeviceEvents, ids, touchAt, ct),
            RecordType.Note => Touch(ctx.Notes, ids, touchAt, ct),
            RecordType.BolusCalculation => Touch(ctx.BolusCalculations, ids, touchAt, ct),
            RecordType.TempBasal => Touch(ctx.TempBasals, ids, touchAt, ct),
            _ => Task.CompletedTask,
        };

    private static async Task Touch<TEntity>(
        IQueryable<TEntity> rows, IReadOnlyList<Guid> ids, DateTime touchAt, CancellationToken ct)
        where TEntity : class, IIdentified, ISystemTimestamped
    {
        foreach (var (group, index) in ids.Order().Chunk(NocturneDbContext.SystemTimestampGroupSize).Select((g, i) => (g, i)))
        {
            var stamp = touchAt.AddMilliseconds(index);
            await rows.Where(e => group.Contains(e.Id))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(e => EF.Property<DateTime>(e, nameof(ISystemTimestamped.SysUpdatedAt)), stamp), ct);
        }
    }
}

/// <summary>Whether a record's link is to be its duplicate group's primary.</summary>
internal readonly record struct PrimaryChange(Guid RecordId, bool IsPrimary);

/// <summary>A row whose flag disagrees with its link, and the flag it holds.</summary>
internal readonly record struct DisagreeingRow(Guid Id, bool IsDuplicate);
