using Moq;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.TestDoubles;

internal static class StateSpanServiceMockExtensions
{
    /// <summary>
    /// Backs the reads and writes the device status decomposer makes with <paramref name="rows"/>, as
    /// the store would: a span's id is its original id when it has one, every read is a copy, and an
    /// original id in <paramref name="deleted"/> is held by a row the user deleted, which no read sees
    /// and which blocks an upsert. A write ends and releases the stored spans as the store does, by the
    /// decisions of <see cref="StateSpanSuccession"/>.
    /// </summary>
    public static List<StateSpan> BacksWith(
        this Mock<IStateSpanService> mock, List<StateSpan> rows, ISet<string>? deleted = null)
    {
        mock
            .Setup(s => s.GetStateSpansAroundAsync(
                It.IsAny<StateSpanCategory>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<IReadOnlyCollection<string?>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateSpanCategory category, DateTime from, DateTime through,
                IReadOnlyCollection<string?> sources, int following, CancellationToken _) =>
            {
                var inCategory = rows.Where(s => s.Category == category).ToList();
                var around = inCategory
                    .Where(s => s.EndTimestamp == null || (s.EndTimestamp >= from && s.StartTimestamp <= through))
                    .Concat(sources.Select(source => string.IsNullOrEmpty(source) ? null : source).Distinct()
                        .SelectMany(source => inCategory
                            .Where(s => s.StartTimestamp > through && (source is null || s.Source == source))
                            .OrderBy(s => s.StartTimestamp)
                            .Take(following)));
                return (IReadOnlyList<StateSpan>)[.. around.Distinct().Select(Copy)];
            });
        mock
            .Setup(s => s.UpsertStateSpansAsync(It.IsAny<IReadOnlyList<StateSpan>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<StateSpan> spans, CancellationToken _) => Upsert(rows, deleted, spans));
        mock
            .Setup(s => s.UpdateStateSpansAsync(It.IsAny<IReadOnlyList<StateSpan>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<StateSpan> spans, CancellationToken _) => Update(rows, spans));
        return rows;
    }

    /// <summary>
    /// Answers the treatment decomposer's span reads and bulk writes through the per-span forms, so a
    /// test sets up and verifies one span at a time: the open suspensions through
    /// <see cref="IStateSpanService.GetStateSpansAsync"/>, every write through
    /// <see cref="IStateSpanService.UpsertStateSpanAsync"/>.
    /// </summary>
    public static Mock<IStateSpanService> AnswersBatchFormsPerSpan(this Mock<IStateSpanService> mock)
    {
        mock
            .Setup(s => s.GetStateSpansAroundAsync(
                It.IsAny<StateSpanCategory>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<IReadOnlyCollection<string?>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (StateSpanCategory category, DateTime _, DateTime _, IReadOnlyCollection<string?> _, int _, CancellationToken ct) =>
                (IReadOnlyList<StateSpan>)[.. await mock.Object.GetStateSpansAsync(
                    category, PumpModeState.Suspended.ToString(), null, null, null, true, 1, 0, true, ct) ?? []]);
        mock
            .Setup(s => s.UpsertStateSpansAsync(It.IsAny<IReadOnlyList<StateSpan>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<StateSpan> spans, CancellationToken ct) =>
            {
                var written = new List<StateSpanUpsert>();
                foreach (var span in spans)
                    written.Add(new StateSpanUpsert(await mock.Object.UpsertStateSpanAsync(span, ct) ?? span, UpsertOutcome.Created));
                return (IReadOnlyList<StateSpanUpsert>)written;
            });
        mock
            .Setup(s => s.UpdateStateSpansAsync(It.IsAny<IReadOnlyList<StateSpan>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<StateSpan> spans, CancellationToken ct) =>
            {
                var written = new List<StateSpan?>();
                foreach (var span in spans)
                    written.Add(await mock.Object.UpsertStateSpanAsync(span, ct) ?? span);
                return (IReadOnlyList<StateSpan?>)written;
            });
        return mock;
    }

    private static IReadOnlyList<StateSpanUpsert> Upsert(
        List<StateSpan> rows, ISet<string>? deleted, IReadOnlyList<StateSpan> spans)
    {
        var stored = Snapshot(rows);
        var batchIds = spans.Select(s => s.OriginalId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var tracked = rows.Where(r => r.OriginalId is { } id && batchIds.Contains(id)).Select(r => EntityOf(r).Id).ToHashSet();
        var moved = new Dictionary<Guid, DateTime>();
        var written = new List<(StateSpan Stored, UpsertOutcome? Outcome)>();
        foreach (var span in spans)
        {
            if (span.OriginalId is { } key && deleted?.Contains(key) == true)
            {
                written.Add((Copy(span), null));
                continue;
            }

            var previous = rows.Find(r => span.OriginalId is not null && r.OriginalId == span.OriginalId);
            var row = Copy(span);
            row.Id = span.OriginalId ?? Guid.NewGuid().ToString();
            row.SupersededById = null;
            if (previous is not null)
            {
                var before = EntityOf(previous);
                var storedSuperseder = before.SupersededById is { } by ? stored.GetValueOrDefault(by) : null;
                var superseder = before.SupersededById is { } pointer && tracked.Contains(pointer)
                    ? CurrentEntity(rows, pointer)
                    : storedSuperseder;
                var (end, supersededBy) = StateSpanSupersessionExtensions.UploadedEnd(
                    EntityOf(row), span, StateSpanSupersessionExtensions.SupersededEnd(before, storedSuperseder), superseder);
                row.EndTimestamp = end;
                row.SupersededById = supersededBy?.ToString();
                var after = EntityOf(row);
                if (StateSpanSuccession.ExclusiveCategories.Contains(before.Category)
                    && (before.StartTimestamp != after.StartTimestamp || before.Category != after.Category)
                    || !StateSpanSuccession.EndsNothing(before) && StateSpanSuccession.EndsNothing(after))
                    moved[before.Id] = before.StartTimestamp;
                rows.Remove(previous);
            }

            rows.Add(row);
            written.Add((row, previous is not null ? UpsertOutcome.Updated : UpsertOutcome.Created));
        }

        Settle(rows, moved, [.. written.Where(w => w.Outcome is not null).Select(w => w.Stored)]);
        return [.. written.Select(w => new StateSpanUpsert(Copy(w.Stored), w.Outcome))];
    }

    /// <summary>
    /// Maps every span of the batch before deciding any end, so a superseder the batch moves is judged where
    /// it now starts, as the store's update does.
    /// </summary>
    private static IReadOnlyList<StateSpan?> Update(List<StateSpan> rows, IReadOnlyList<StateSpan> spans)
    {
        var stored = Snapshot(rows);
        var mapped = new List<(StateSpan Row, StateSpan Sent, DateTime? SupersededEnd, DateTime StartBefore)>();
        var written = new List<StateSpan?>();
        foreach (var span in spans)
        {
            var index = rows.FindIndex(r => r.Id == span.Id);
            if (index < 0)
            {
                written.Add(null);
                continue;
            }

            var current = rows[index];
            var row = Copy(span);
            row.EndTimestamp = current.EndTimestamp;
            row.SupersededById = current.SupersededById;
            if (span.EndTimestamp != current.EndTimestamp)
            {
                var before = EntityOf(current);
                var storedSuperseder = before.SupersededById is { } by ? stored.GetValueOrDefault(by) : null;
                mapped.Add((row, span, StateSpanSupersessionExtensions.SupersededEnd(before, storedSuperseder), before.StartTimestamp));
            }

            rows[index] = row;
            written.Add(row);
        }

        var tracked = written.OfType<StateSpan>().Select(s => EntityOf(s).Id).ToHashSet();
        var moved = new Dictionary<Guid, DateTime>();
        var leftOpen = new List<StateSpan>();
        foreach (var (row, sent, supersededEnd, startBefore) in mapped)
        {
            var entity = EntityOf(row);
            var superseder = entity.SupersededById is { } pointer
                ? (tracked.Contains(pointer) ? CurrentEntity(rows, pointer) : null) ?? stored.GetValueOrDefault(pointer)
                : null;
            var (end, supersededBy) = StateSpanSupersessionExtensions.UploadedEnd(entity, sent, supersededEnd, superseder);
            row.EndTimestamp = end;
            row.SupersededById = supersededBy?.ToString();
            if (supersededEnd is not null && end is null)
                leftOpen.Add(row);
            if (StateSpanSuccession.EndsNothing(EntityOf(row)))
                moved[entity.Id] = startBefore;
        }

        Settle(rows, moved, leftOpen);
        return [.. written.Select(s => s is null ? null : Copy(s))];
    }

    /// <summary>
    /// What <see cref="StateSpanSupersessionExtensions.SaveUploadAsync"/> does once the rows are written:
    /// each span in <paramref name="moved"/> (with the start it had) first releases the rows it ended, then
    /// each exclusive span of <paramref name="arrived"/>, and each row it may now succeed, ends at its
    /// first live successor.
    /// </summary>
    private static void Settle(List<StateSpan> rows, Dictionary<Guid, DateTime> moved, IReadOnlyList<StateSpan> arrived)
    {
        var arrivedEntities = arrived.Select(EntityOf)
            .Where(e => StateSpanSuccession.ExclusiveCategories.Contains(e.Category))
            .ToList();
        if (moved.Count == 0 && arrivedEntities.Count == 0)
            return;

        var (entities, rowsById) = Mapped(rows);
        foreach (var entity in entities.Values.Where(e => e.SupersededById is { } by
                                                          && moved.ContainsKey(by) && !moved.ContainsKey(e.Id)).ToList())
        {
            var row = rowsById[entity.Id];
            if (!StateSpanSuccession.EndsAtSuperseder(entity, moved[entity.SupersededById!.Value]))
            {
                row.SupersededById = null;
                continue;
            }

            var (end, supersededBy) = StateSpanSuccession.EndAt(
                FirstFollowing(entities.Values, entity, e => !moved.ContainsKey(e.Id)));
            row.EndTimestamp = end;
            row.SupersededById = supersededBy?.ToString();
        }

        (entities, rowsById) = Mapped(rows);
        var reaches = StateSpanSuccession.Reaches(arrivedEntities);
        var spans = entities.Values.Where(reaches).ToDictionary(e => e.Id);
        foreach (var entity in arrivedEntities)
            spans.TryAdd(entity.Id, entities[entity.Id]);

        var ends = spans.Values
            .Where(s => StateSpanSuccession.IsMovable(s, entities))
            .Where(s => StateSpanSuccession.ExclusiveCategories.Contains(s.Category))
            .Select(s => (s.Id, End: StateSpanSuccession.EndAt(FirstFollowing(entities.Values, s, _ => true))))
            .ToList();
        foreach (var (id, end) in ends)
        {
            rowsById[id].EndTimestamp = end.End;
            rowsById[id].SupersededById = end.SupersededById?.ToString();
        }
    }

    private static (Dictionary<Guid, StateSpanEntity> Entities, Dictionary<Guid, StateSpan> Rows) Mapped(List<StateSpan> rows)
    {
        var pairs = rows.Select(r => (Row: r, Entity: EntityOf(r))).ToList();
        return (pairs.ToDictionary(p => p.Entity.Id, p => p.Entity), pairs.ToDictionary(p => p.Entity.Id, p => p.Row));
    }

    private static Dictionary<Guid, StateSpanEntity> Snapshot(List<StateSpan> rows) => Mapped(rows).Entities;

    private static StateSpanEntity CurrentEntity(List<StateSpan> rows, Guid id) =>
        rows.Select(EntityOf).First(e => e.Id == id);

    private static StateSpanEntity? FirstFollowing(
        IEnumerable<StateSpanEntity> entities, StateSpanEntity open, Func<StateSpanEntity, bool> eligible) =>
        StateSpanSuccession.FirstFollowing(
            open,
            MapperHelpers.DeserializeJson<Dictionary<string, object>>(open.MetadataJson),
            entities.OrderBy(e => e.StartTimestamp).ThenBy(e => e.Id),
            eligible);

    private static StateSpanEntity EntityOf(StateSpan span)
    {
        var entity = StateSpanMapper.ToEntity(span);
        entity.SupersededById = Guid.TryParse(span.SupersededById, out var by) ? by : null;
        return entity;
    }

    private static StateSpan Copy(StateSpan span) => new()
    {
        Id = span.Id,
        Category = span.Category,
        State = span.State,
        StartTimestamp = span.StartTimestamp,
        EndTimestamp = span.EndTimestamp,
        Source = span.Source,
        Metadata = span.Metadata is null ? null : new Dictionary<string, object>(span.Metadata),
        OriginalId = span.OriginalId,
        SupersededById = span.SupersededById,
    };
}
