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
/// its primary (<see cref="ReadVisibilityFilter.ExcludeNonPrimary{TEntity}"/>), so a deleted
/// primary hides every other source's copy with it; and the promoted copy's own row did not
/// change, so a v3 history client would never be sent it.
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
    public static RecordType? RecordTypeOf<TEntity>() =>
        RecordTypes.TryGetValue(typeof(TEntity), out var recordType) ? recordType : null;

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
    /// <see cref="DeduplicationService.PickSurvivor(IEnumerable{LinkedRecordEntity}, Func{Guid, bool})"/>,
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
        var live = await LiveIdsAsync(ctx, recordType, rows.Select(r => r.RecordId).Distinct().ToArray(), ct);

        var demote = new List<Guid>();
        var promote = new List<Guid>();
        var promotedLive = new List<Guid>();
        foreach (var group in rows.GroupBy(r => r.CanonicalId))
        {
            var survivor = DeduplicationService.PickSurvivor(group, live.Contains);
            if (survivor.IsPrimary)
                continue;

            demote.AddRange(group.Where(r => r.IsPrimary).Select(r => r.Id));
            promote.Add(survivor.Id);
            if (live.Contains(survivor.RecordId))
                promotedLive.Add(survivor.RecordId);
        }

        if (demote.Count > 0)
            await ctx.LinkedRecords.Where(lr => demote.Contains(lr.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(lr => lr.IsPrimary, false), ct);
        if (promote.Count > 0)
            await ctx.LinkedRecords.Where(lr => promote.Contains(lr.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(lr => lr.IsPrimary, true), ct);

        return promotedLive;
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
