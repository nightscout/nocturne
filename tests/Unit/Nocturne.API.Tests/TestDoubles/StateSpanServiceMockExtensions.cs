using Moq;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.TestDoubles;

internal static class StateSpanServiceMockExtensions
{
    /// <summary>
    /// Backs the reads and writes the device status decomposer makes with <paramref name="rows"/>, as
    /// the store would: a span's id is its original id when it has one, every read is a copy, and an
    /// original id in <paramref name="deleted"/> is held by a row the user deleted, which no read sees
    /// and which blocks an upsert.
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
            .ReturnsAsync((IReadOnlyList<StateSpan> spans, CancellationToken _) =>
                (IReadOnlyList<StateSpanUpsert>)[.. spans.Select(span =>
                {
                    if (span.OriginalId is { } key && deleted?.Contains(key) == true)
                        return new StateSpanUpsert(Copy(span), null);

                    var stored = Copy(span);
                    stored.Id = span.OriginalId ?? Guid.NewGuid().ToString();
                    var replaced = rows.RemoveAll(r => span.OriginalId is not null && r.OriginalId == span.OriginalId) > 0;
                    rows.Add(stored);
                    return new StateSpanUpsert(Copy(stored), replaced ? UpsertOutcome.Updated : UpsertOutcome.Created);
                })]);
        mock
            .Setup(s => s.UpdateStateSpansAsync(It.IsAny<IReadOnlyList<StateSpan>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<StateSpan> spans, CancellationToken _) =>
                (IReadOnlyList<StateSpan?>)[.. spans.Select(span =>
                {
                    var index = rows.FindIndex(r => r.Id == span.Id);
                    if (index < 0)
                        return null;
                    rows[index] = Copy(span);
                    return Copy(span);
                })]);
        return rows;
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
