using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Keeps supersession true when a span is removed (soft-deleted or purged) or arrives outside the upsert
/// (restored, or written by a replace): an open span of an exclusive category ends at its first live
/// successor (<see cref="StateSpanSuccession"/>).
/// </summary>
/// <remarks>
/// Supersession ends only an open span, so the end it gave is told apart by the pointer and the
/// superseder's start, with no record of the span's own end. Only such an end, or an open span, is
/// moved; an uploaded end never is. The foreign key's <c>SetNull</c> ran as a referential trigger,
/// which ignores Row Level Security, so neither a removal nor an arrival is narrowed by the caller's
/// 24 hour history clamp: a span the clamp hides would be left ended by a removed span, or open beside
/// an arrived one, and its successor may be hidden too. The clamp is lifted
/// (<see cref="HistoryClampLiftExtensions"/>) for reads that only decide the writes, and for the
/// writes, which set the end, the pointer and the row's update stamp, the stamp so that a history
/// client is sent the changed row. Both write by statement and track nothing.
/// </remarks>
internal static class StateSpanSupersessionExtensions
{
    /// <summary>What a span was written with.</summary>
    internal readonly record struct WrittenEnd(DateTime? End, Guid? SupersededById, DateTime UpdatedAt);

    /// <summary>
    /// Undoes what <paramref name="removedIds"/> did to the other live spans they superseded. A span
    /// whose end is its superseder's start is reopened and ended at its next live successor other than
    /// a removed one, if it has one. A span whose end is another loses only
    /// <see cref="StateSpanEntity.SupersededById"/>. A soft delete calls it once every row is stamped,
    /// a purge before its rows go, while the superseders' starts can still be read.
    /// </summary>
    /// <param name="ctx">The context the spans are removed through.</param>
    /// <param name="removedIds">The spans removed.</param>
    /// <param name="purged">
    /// Whether they are being purged. The purge takes the pointer of a soft-deleted span that names one,
    /// which is all a later restore could tell its superseded end by, so such an end is cleared with it.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The rows written.</returns>
    public static async Task<int> ReopenSupersededAsync(
        this NocturneDbContext ctx, IReadOnlyCollection<Guid> removedIds, bool purged, CancellationToken ct)
    {
        if (removedIds.Count == 0)
            return 0;

        var removed = removedIds.ToHashSet();
        var rowIds = removed.ToArray();
        var ids = rowIds.Select(id => (Guid?)id).ToArray();
        return await ctx.WriteWithHistoryClampLiftedAsync(
            async token =>
            {
                await ctx.PlanForEachArrayAsync(ids.Length, token);
                var pointing = purged ? ctx.StateSpans.IncludingDeleted() : ctx.StateSpans;
                var superseded = await pointing.AsNoTracking()
                    .Where(s => ids.Contains(s.SupersededById) && !rowIds.Contains(s.Id))
                    .Join(
                        ctx.StateSpans.IncludingDeleted(),
                        s => s.SupersededById,
                        d => (Guid?)d.Id,
                        (s, d) => new { Span = s, SupersederStart = d.StartTimestamp })
                    .ToListAsync(token);
                if (superseded.Count == 0)
                    return 0;

                var touchedAt = NocturneDbContext.UtcNowAtStoredPrecision();
                var kept = superseded
                    .Where(r => r.Span.DeletedAt == null && r.Span.EndTimestamp != r.SupersederStart)
                    .Select(r => r.Span.Id)
                    .ToArray();
                var deletedReopened = superseded
                    .Where(r => r.Span.DeletedAt != null && r.Span.EndTimestamp == r.SupersederStart)
                    .Select(r => r.Span.Id)
                    .ToArray();
                var written = await ClearAsync(kept, s => s.SetProperty(e => e.SupersededById, (Guid?)null));
                written += await ClearAsync(deletedReopened, s => s
                    .SetProperty(e => e.EndTimestamp, (DateTime?)null)
                    .SetProperty(e => e.SupersededById, (Guid?)null));
                var reopened = superseded
                    .Where(r => r.Span.DeletedAt == null && r.Span.EndTimestamp == r.SupersederStart)
                    .Select(r => r.Span)
                    .ToList();
                return written + (await EndAtSuccessorsAsync(
                    ctx, reopened, s => !removed.Contains(s.Id), writeUnchanged: true, touchedAt, token)).Count;

                async Task<int> ClearAsync(
                    Guid[] targets, Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<StateSpanEntity>> set) =>
                    targets.Length == 0
                        ? 0
                        : await ctx.StateSpans.IncludingDeleted().Where(s => targets.Contains(s.Id)).ExecuteUpdateAsync(
                            s =>
                            {
                                set(s);
                                s.SetProperty(e => e.UpdatedAt, touchedAt);
                            },
                            token);
            },
            ct);
    }

    /// <summary>
    /// Ends at its first live successor each open or superseded span whose first successor
    /// <paramref name="arrivedIds"/> may now be: those starting before an arrived span of their
    /// category (in PumpMode, of their state) that are open or ended after its start, and, unless
    /// <paramref name="asGiven"/>, the arrived spans themselves.
    /// </summary>
    /// <param name="ctx">The context the spans arrived through.</param>
    /// <param name="arrivedIds">Spans restored, or written as given.</param>
    /// <param name="asGiven">Whether the arrived spans keep the ends they were written with.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>What each span written was written with, by id.</returns>
    public static async Task<IReadOnlyDictionary<Guid, WrittenEnd>> SupersedeByArrivedAsync(
        this NocturneDbContext ctx, IReadOnlyCollection<Guid> arrivedIds, bool asGiven, CancellationToken ct)
    {
        if (arrivedIds.Count == 0)
            return new Dictionary<Guid, WrittenEnd>();

        var ids = arrivedIds.ToArray();
        return await ctx.WriteWithHistoryClampLiftedAsync(
            async token =>
            {
                await ctx.PlanForEachArrayAsync(ids.Length, token);
                var arrived = (await ctx.StateSpans.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync(token))
                    .Where(s => StateSpanSuccession.ExclusiveCategories.Contains(s.Category))
                    .ToList();
                if (arrived.Count == 0)
                    return (IReadOnlyDictionary<Guid, WrittenEnd>)new Dictionary<Guid, WrittenEnd>();

                var spans = await OverlappingAsync(ctx, arrived, token);
                var arrivedIdSet = arrived.Select(s => s.Id).ToHashSet();
                if (!asGiven)
                {
                    foreach (var span in arrived)
                        spans.TryAdd(span.Id, span);
                }

                var pointers = spans.Values.Select(s => s.SupersededById).OfType<Guid>().Distinct().ToArray();
                var supersederStarts = await ctx.StateSpans.IncludingDeleted().AsNoTracking()
                    .Where(s => pointers.Contains(s.Id))
                    .ToDictionaryAsync(s => s.Id, s => s.StartTimestamp, token);
                var movable = spans.Values
                    .Where(s => !(asGiven && arrivedIdSet.Contains(s.Id)))
                    .Where(s => s.EndTimestamp == null
                        || (s.SupersededById is { } by && supersederStarts.GetValueOrDefault(by) == s.EndTimestamp))
                    .ToList();
                return await EndAtSuccessorsAsync(
                    ctx, movable, _ => true, writeUnchanged: false, NocturneDbContext.UtcNowAtStoredPrecision(), token);
            },
            ct);
    }

    /// <summary>
    /// Gives the tracked <paramref name="spans"/> the ends <see cref="SupersedeByArrivedAsync"/> wrote
    /// to them, as their stored values, so that what is answered is what is stored.
    /// </summary>
    public static void AcceptWrittenEnds(
        this NocturneDbContext ctx, IReadOnlyDictionary<Guid, WrittenEnd> written, IEnumerable<StateSpanEntity> spans)
    {
        foreach (var span in spans)
        {
            if (!written.TryGetValue(span.Id, out var end))
                continue;

            var entry = ctx.Entry(span);
            Accept(entry.Property(e => e.EndTimestamp), end.End);
            Accept(entry.Property(e => e.SupersededById), end.SupersededById);
            Accept(entry.Property(e => e.UpdatedAt), end.UpdatedAt);
        }

        static void Accept<T>(Microsoft.EntityFrameworkCore.ChangeTracking.PropertyEntry<StateSpanEntity, T> property, T value)
        {
            property.CurrentValue = value;
            property.OriginalValue = value;
            property.IsModified = false;
        }
    }

    /// <summary>
    /// The live spans that start before an arrived span of their line (<see cref="StateSpanSuccession.Key"/>)
    /// and are open or end after its start, by id: one read per category of the time the arrived spans
    /// cover, matched to each line's arrived starts in memory.
    /// </summary>
    private static async Task<Dictionary<Guid, StateSpanEntity>> OverlappingAsync(
        NocturneDbContext ctx, IReadOnlyList<StateSpanEntity> arrived, CancellationToken ct)
    {
        var found = new Dictionary<Guid, StateSpanEntity>();
        foreach (var category in arrived.GroupBy(s => s.Category))
        {
            var starts = category
                .GroupBy(s => StateSpanSuccession.Key(s.Category, s.State, default).State)
                .ToDictionary(
                    line => line.Key ?? string.Empty,
                    line => line.Select(s => s.StartTimestamp).Distinct().Order().ToArray());
            var sameStateOnly = StateSpanSuccession.SameStateOnly(category.Key);
            var earliest = category.Min(s => s.StartTimestamp);
            var latest = category.Max(s => s.StartTimestamp);
            var name = category.Key;
            var spans = ctx.StateSpans.AsNoTracking()
                .Where(s => s.Category == name
                    && s.StartTimestamp < latest
                    && (s.EndTimestamp == null || s.EndTimestamp > earliest));
            await foreach (var span in spans.AsAsyncEnumerable().WithCancellation(ct))
            {
                if (starts.TryGetValue(sameStateOnly ? span.State : string.Empty, out var lineStarts)
                    && StartsWithin(lineStarts, span.StartTimestamp, span.EndTimestamp))
                    found.TryAdd(span.Id, span);
            }
        }

        return found;
    }

    /// <summary>Whether a start in the sorted <paramref name="starts"/> lies after <paramref name="after"/> and before <paramref name="before"/>, if any.</summary>
    private static bool StartsWithin(DateTime[] starts, DateTime after, DateTime? before)
    {
        var index = Array.BinarySearch(starts, after);
        var next = index >= 0 ? index + 1 : ~index;
        return next < starts.Length && (before is null || starts[next] < before);
    }

    /// <summary>
    /// Writes each of <paramref name="spans"/> ended at its first live <paramref name="eligible"/>
    /// successor, or open when it has none: every one when <paramref name="writeUnchanged"/>, else only
    /// those whose end or pointer moves. One statement per end written.
    /// </summary>
    /// <returns>What each span written was written with, by id.</returns>
    private static async Task<Dictionary<Guid, WrittenEnd>> EndAtSuccessorsAsync(
        NocturneDbContext ctx, IReadOnlyList<StateSpanEntity> spans, Func<StateSpanEntity, bool> eligible,
        bool writeUnchanged, DateTime touchedAt, CancellationToken ct)
    {
        var successors = await SuccessorsAsync(ctx, spans, eligible, ct);
        var written = new Dictionary<Guid, WrittenEnd>();
        foreach (var group in spans
                     .Select(s => (s.Id, s.EndTimestamp, s.SupersededById,
                         Target: successors.TryGetValue(s.Id, out var next)
                             ? ((DateTime?)next.StartTimestamp, (Guid?)next.Id)
                             : (null, null)))
                     .Where(s => writeUnchanged || s.Target != (s.EndTimestamp, s.SupersededById))
                     .GroupBy(s => s.Target, s => s.Id))
        {
            var (end, by) = group.Key;
            var targets = group.ToArray();
            await ctx.StateSpans.Where(s => targets.Contains(s.Id)).ExecuteUpdateAsync(
                s => s
                    .SetProperty(e => e.EndTimestamp, end)
                    .SetProperty(e => e.SupersededById, by)
                    .SetProperty(e => e.UpdatedAt, touchedAt),
                ct);
            foreach (var id in targets)
                written[id] = new WrittenEnd(end, by, touchedAt);
        }

        return written;
    }

    /// <summary>
    /// The first live <paramref name="eligible"/> successor of each of <paramref name="spans"/> that has
    /// one, by span id.
    /// </summary>
    private static async Task<Dictionary<Guid, StateSpanEntity>> SuccessorsAsync(
        NocturneDbContext ctx, IReadOnlyList<StateSpanEntity> spans, Func<StateSpanEntity, bool> eligible,
        CancellationToken ct)
    {
        var exclusive = spans.Where(s => StateSpanSuccession.ExclusiveCategories.Contains(s.Category)).ToList();
        var found = new Dictionary<Guid, StateSpanEntity>();
        if (exclusive.Count == 0)
            return found;

        var candidates = await StateSpanSuccession.LoadCandidatesAsync(
            ctx,
            [.. exclusive.Select(s => StateSpanSuccession.Key(s.Category, s.State, s.StartTimestamp)).Distinct()],
            ct);
        foreach (var span in exclusive)
        {
            var successor = await StateSpanSuccession.FirstStoredAsync(
                ctx,
                span,
                MapperHelpers.DeserializeJson<Dictionary<string, object>>(span.MetadataJson),
                candidates[StateSpanSuccession.Key(span.Category, span.State, span.StartTimestamp)],
                eligible,
                null,
                ct);
            if (successor is not null)
                found[span.Id] = successor;
        }

        return found;
    }
}
