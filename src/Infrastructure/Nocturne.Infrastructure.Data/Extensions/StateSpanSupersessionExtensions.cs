using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Keeps supersession true when a span is removed (soft-deleted or purged), arrives outside the upsert
/// (restored, or written by a replace), or is rewritten by an upload (<see cref="UploadedEnd"/>): an open
/// span of an exclusive category ends at its first live successor (<see cref="StateSpanSuccession"/>).
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
    /// <returns>What each span written was written with, by id.</returns>
    public static async Task<IReadOnlyDictionary<Guid, WrittenEnd>> ReopenSupersededAsync(
        this NocturneDbContext ctx, IReadOnlyCollection<Guid> removedIds, bool purged, CancellationToken ct)
    {
        if (removedIds.Count == 0)
            return new Dictionary<Guid, WrittenEnd>();

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
                    return (IReadOnlyDictionary<Guid, WrittenEnd>)new Dictionary<Guid, WrittenEnd>();

                var touchedAt = NocturneDbContext.UtcNowAtStoredPrecision();
                var kept = superseded
                    .Where(r => r.Span.DeletedAt == null && r.Span.EndTimestamp != r.SupersederStart)
                    .Select(r => r.Span)
                    .ToList();
                var deletedReopened = superseded
                    .Where(r => r.Span.DeletedAt != null && r.Span.EndTimestamp == r.SupersederStart)
                    .Select(r => r.Span)
                    .ToList();
                await ClearAsync(kept, s => s.SetProperty(e => e.SupersededById, (Guid?)null));
                await ClearAsync(deletedReopened, s => s
                    .SetProperty(e => e.EndTimestamp, (DateTime?)null)
                    .SetProperty(e => e.SupersededById, (Guid?)null));
                var reopened = superseded
                    .Where(r => r.Span.DeletedAt == null && r.Span.EndTimestamp == r.SupersederStart)
                    .Select(r => r.Span)
                    .ToList();
                var written = await EndAtSuccessorsAsync(
                    ctx, reopened, s => !removed.Contains(s.Id), writeUnchanged: true, touchedAt, token);
                foreach (var span in kept)
                    written[span.Id] = new WrittenEnd(span.EndTimestamp, null, touchedAt);
                foreach (var span in deletedReopened)
                    written[span.Id] = new WrittenEnd(null, null, touchedAt);
                return written;

                async Task ClearAsync(
                    List<StateSpanEntity> spans,
                    Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<StateSpanEntity>> set)
                {
                    if (spans.Count == 0)
                        return;

                    Guid[] targets = [.. spans.Select(s => s.Id)];
                    await ctx.StateSpans.IncludingDeleted().Where(s => targets.Contains(s.Id)).ExecuteUpdateAsync(
                        s =>
                        {
                            set(s);
                            s.SetProperty(e => e.UpdatedAt, touchedAt);
                        },
                        token);
                }
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

                var superseders = await ctx.SupersedersAsync(spans.Values, token);
                var movable = spans.Values
                    .Where(s => !(asGiven && arrivedIdSet.Contains(s.Id)))
                    .Where(s => s.EndTimestamp == null || SupersededEnd(s, superseders) is not null)
                    .ToList();
                return await EndAtSuccessorsAsync(
                    ctx, movable, _ => true, writeUnchanged: false, NocturneDbContext.UtcNowAtStoredPrecision(), token);
            },
            ct);
    }

    /// <summary>The span each of <paramref name="spans"/> names as its superseder, live or not, as stored, by its id.</summary>
    public static async Task<Dictionary<Guid, StateSpanEntity>> SupersedersAsync(
        this NocturneDbContext ctx, IEnumerable<StateSpanEntity> spans, CancellationToken ct)
    {
        var pointers = spans.Select(s => s.SupersededById).OfType<Guid>().Distinct().ToArray();
        return pointers.Length == 0
            ? []
            : await ctx.StateSpans.IncludingDeleted().AsNoTracking()
                .Where(s => pointers.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, ct);
    }

    /// <summary>
    /// The end supersession gave <paramref name="span"/>, the start of the span it names, when its end
    /// is that; null for an uploaded end or none.
    /// </summary>
    public static DateTime? SupersededEnd(StateSpanEntity span, IReadOnlyDictionary<Guid, StateSpanEntity> superseders) =>
        SupersededEnd(span, span.SupersededById is { } by ? superseders.GetValueOrDefault(by) : null);

    /// <inheritdoc cref="SupersededEnd(StateSpanEntity, IReadOnlyDictionary{Guid, StateSpanEntity})"/>
    /// <param name="span">The span.</param>
    /// <param name="superseder">The span it names, as stored, or null.</param>
    public static DateTime? SupersededEnd(StateSpanEntity span, StateSpanEntity? superseder) =>
        superseder is not null && span.SupersededById == superseder.Id && superseder.StartTimestamp == span.EndTimestamp
            ? superseder.StartTimestamp
            : null;

    /// <summary>
    /// The end and pointer a stored span keeps when an upload rewrites it. The uploaded end, unless the
    /// span had a superseded end and the upload still reaches the span that gave it
    /// (<see cref="StateSpanSuccession.Follows"/>, at that span's start as it now stands). Then it keeps
    /// that end, or, when that span has moved, it is left open with no pointer, for the writer to end at
    /// its first successor. An uploaded end at or before the superseder's start is the upload's own.
    /// </summary>
    /// <param name="row">The stored row, with every other field of the upload already mapped onto it.</param>
    /// <param name="uploaded">The span uploaded.</param>
    /// <param name="supersededEnd">The row's <see cref="SupersededEnd(StateSpanEntity, StateSpanEntity?)"/> before the upload.</param>
    /// <param name="superseder">The span the row named before the upload, as it now stands.</param>
    public static (DateTime? End, Guid? SupersededById) UploadedEnd(
        StateSpanEntity row, StateSpan uploaded, DateTime? supersededEnd, StateSpanEntity? superseder)
    {
        if (supersededEnd is not { } ended || superseder is null
            || !StateSpanSuccession.Follows(row, uploaded.Metadata, superseder)
            || uploaded.EndTimestamp <= superseder.StartTimestamp)
            return (uploaded.EndTimestamp, null);

        return superseder.StartTimestamp == ended ? (ended, superseder.Id) : (null, null);
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
            if (written.TryGetValue(span.Id, out var end))
                Accept(ctx.Entry(span), end);
        }
    }

    /// <summary>
    /// Saves the spans an upload wrote and settles them with their neighbours, in one transaction. Each of
    /// <paramref name="moved"/> first releases the spans it superseded (<see cref="ReopenSupersededAsync"/>)
    /// while the store still holds the start it ended them at. After the save, every one of
    /// <paramref name="written"/> of an exclusive category, and every span it may now succeed, ends at its
    /// first live successor (<see cref="SupersedeByArrivedAsync"/>). The tracked spans are left as stored.
    /// </summary>
    /// <remarks>
    /// A tracked span the release wrote takes the release's end and pointer when the upload left both as it
    /// read them. Otherwise the upload's, which <see cref="UploadedEnd"/> judged against the superseder the
    /// span named before the release, are written over the release's.
    /// </remarks>
    /// <param name="ctx">The context that tracks the upload's spans.</param>
    /// <param name="moved">Stored spans whose start or category the upload changes.</param>
    /// <param name="written">
    /// The spans, as mapped, that settle after the save: every one an upload wrote, or the ones a fold left open.
    /// </param>
    /// <param name="save">Saves the tracked changes.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>What <paramref name="save"/> returned.</returns>
    public static async Task<int> SaveUploadAsync(
        this NocturneDbContext ctx,
        IReadOnlyCollection<Guid> moved,
        IEnumerable<StateSpanEntity> written,
        Func<CancellationToken, Task<int>> save,
        CancellationToken ct)
    {
        Guid[] arrived = [.. written
            .Where(s => StateSpanSuccession.ExclusiveCategories.Contains(s.Category))
            .Select(s => s.Id)
            .Distinct()];
        if (moved.Count == 0 && arrived.Length == 0)
            return await save(ct);

        return await ctx.ExecuteInTransactionAsync(async token =>
        {
            var released = await ctx.ReopenSupersededAsync(moved, purged: false, token);
            foreach (var entry in ctx.ChangeTracker.Entries<StateSpanEntity>().ToList())
            {
                if (released.TryGetValue(entry.Entity.Id, out var end))
                    TakeReleased(entry, end);
            }

            var saved = await save(token);
            ctx.AcceptWrittenEnds(
                await ctx.SupersedeByArrivedAsync(arrived, asGiven: false, token),
                [.. ctx.ChangeTracker.Entries<StateSpanEntity>().Select(e => e.Entity)]);
            return saved;
        }, ct: ct);
    }

    private static void TakeReleased(EntityEntry<StateSpanEntity> entry, WrittenEnd released)
    {
        var end = entry.Property(e => e.EndTimestamp);
        var pointer = entry.Property(e => e.SupersededById);
        if (end.IsModified || pointer.IsModified)
        {
            Stored(end, released.End);
            Stored(pointer, released.SupersededById);
            // The save stamps a span it writes; one it no longer writes keeps the release's stamp.
            if (entry.Properties.Any(p => p.IsModified))
                return;
        }

        Accept(entry, released);

        static void Stored<T>(PropertyEntry<StateSpanEntity, T> property, T value)
        {
            property.OriginalValue = value;
            property.IsModified = !EqualityComparer<T>.Default.Equals(property.CurrentValue, value);
        }
    }

    private static void Accept(EntityEntry<StateSpanEntity> entry, WrittenEnd written)
    {
        Set(entry.Property(e => e.EndTimestamp), written.End);
        Set(entry.Property(e => e.SupersededById), written.SupersededById);
        Set(entry.Property(e => e.UpdatedAt), written.UpdatedAt);

        static void Set<T>(PropertyEntry<StateSpanEntity, T> property, T value)
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
    /// those whose end or pointer moves. One statement writes every end.
    /// </summary>
    /// <returns>What each span written was written with, by id.</returns>
    private static async Task<Dictionary<Guid, WrittenEnd>> EndAtSuccessorsAsync(
        NocturneDbContext ctx, IReadOnlyList<StateSpanEntity> spans, Func<StateSpanEntity, bool> eligible,
        bool writeUnchanged, DateTime touchedAt, CancellationToken ct)
    {
        var successors = await SuccessorsAsync(ctx, spans, eligible, ct);
        var moves = spans
            .Select(s => (s.Id, Was: (s.EndTimestamp, s.SupersededById),
                Target: successors.TryGetValue(s.Id, out var next)
                    ? ((DateTime?)next.StartTimestamp, (Guid?)next.Id)
                    : (null, null)))
            .Where(s => writeUnchanged || s.Target != s.Was)
            .ToList();
        if (moves.Count == 0)
            return [];

        Guid[] ids = [.. moves.Select(m => m.Id)];
        DateTime?[] ends = [.. moves.Select(m => m.Target.Item1)];
        Guid?[] pointers = [.. moves.Select(m => m.Target.Item2)];
        await ctx.PlanForEachArrayAsync(ids.Length, ct);
        await ctx.StateSpans.Where(s => ids.Contains(s.Id)).ExecuteUpdateAsync(
            s => s
                .SetProperty(e => e.EndTimestamp, e => ends[Array.IndexOf(ids, e.Id)])
                .SetProperty(e => e.SupersededById, e => pointers[Array.IndexOf(ids, e.Id)])
                .SetProperty(e => e.UpdatedAt, touchedAt),
            ct);
        return moves.ToDictionary(m => m.Id, m => new WrittenEnd(m.Target.Item1, m.Target.Item2, touchedAt));
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
                ct);
            if (successor is not null)
                found[span.Id] = successor;
        }

        return found;
    }
}
