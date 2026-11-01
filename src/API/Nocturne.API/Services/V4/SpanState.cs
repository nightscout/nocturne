using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.V4;

/// <summary>
/// The spans a decomposer's folds read and write: those read before the batch and those the folds
/// open. A read span the folds change is written back by id; an opened one is upserted by its original
/// id, as a span written on its own would be.
/// </summary>
internal sealed class SpanState
{
    private readonly Dictionary<StateSpanCategory, List<StateSpan>> _byCategory;

    public SpanState(IEnumerable<StateSpan> read) =>
        _byCategory = read.GroupBy(s => s.Category).ToDictionary(g => g.Key, g => g.ToList());

    /// <summary>The spans of <paramref name="category"/>, read or opened.</summary>
    public List<StateSpan> this[StateSpanCategory category] =>
        _byCategory.TryGetValue(category, out var spans) ? spans : _byCategory[category] = [];

    public List<StateSpan> Opened { get; } = [];

    public List<StateSpan> Changed { get; } = [];

    public void Change(StateSpan span)
    {
        if (!Opened.Contains(span) && !Changed.Contains(span))
            Changed.Add(span);
    }

    /// <summary>
    /// An opened span takes the place in memory of the span already keyed to its original id, which
    /// the upsert overwrites in the store.
    /// </summary>
    public void Open(StateSpan span)
    {
        var category = this[span.Category];
        if (!string.IsNullOrEmpty(span.OriginalId))
            category.RemoveAll(s => s.OriginalId == span.OriginalId);
        category.Add(span);
        Opened.Add(span);
    }

    /// <summary>
    /// The read spans the folds changed, in one update by id, then the spans they opened, in one upsert
    /// in the order they were opened. An opened span a user-deleted row blocks wrote nothing and is
    /// counted in <see cref="DecompositionResult.SkippedDeleted"/>. Each written span takes the owner of
    /// the span it was written from.
    /// </summary>
    /// <param name="refuse">
    /// Given, a value the store refuses (<see cref="StoreRefusal"/>) fails only its own span: the write
    /// that held it is made again one span at a time, once, and each span refused is passed here and
    /// writes nothing. Not given, the refusal fails the write.
    /// </param>
    public async Task WriteAsync(
        IStateSpanService service, DecompositionResult result, Dictionary<StateSpan, Guid> owners,
        CancellationToken ct, Action<StateSpan, DbUpdateException>? refuse = null)
    {
        if (Changed.Count > 0)
            await WriteEachRefusedAloneAsync(
                Changed, spans => service.UpdateStateSpansAsync(spans, ct), FoldUpdated, refuse);

        if (Opened.Count > 0)
            await WriteEachRefusedAloneAsync(
                Opened, spans => service.UpsertStateSpansAsync(spans, ct), FoldUpserted, refuse);

        void FoldUpdated(IReadOnlyList<StateSpan> spans, IReadOnlyList<StateSpan?> updated)
        {
            foreach (var (span, written) in spans.Zip(updated))
            {
                if (written is null)
                    continue;
                if (owners.TryGetValue(span, out var owner))
                    owners[written] = owner;
                result.UpdatedRecords.Add(written);
            }
        }

        void FoldUpserted(IReadOnlyList<StateSpan> spans, IReadOnlyList<StateSpanUpsert?> upserted)
        {
            foreach (var (span, upsert) in spans.Zip(upserted))
            {
                if (upsert is null)
                    continue;
                var (written, outcome) = upsert;
                var records = outcome switch
                {
                    UpsertOutcome.Created => result.CreatedRecords,
                    UpsertOutcome.Updated => result.UpdatedRecords,
                    UpsertOutcome.Unchanged => result.UnchangedRecords,
                    _ => null,
                };
                if (records is null)
                {
                    result.SkippedDeleted++;
                    continue;
                }
                if (owners.TryGetValue(span, out var owner))
                    owners[written] = owner;
                records.Add(written);
            }
        }
    }

    /// <summary>
    /// One write of <paramref name="spans"/>, or, when <paramref name="refuse"/> is given and the store
    /// refuses a value, one write per span. Each write is folded as it lands, one that threw after its
    /// commit included, so a failure part way through the spans written one at a time keeps those
    /// before it.
    /// </summary>
    private static async Task WriteEachRefusedAloneAsync<TWritten>(
        List<StateSpan> spans,
        Func<IReadOnlyList<StateSpan>, Task<IReadOnlyList<TWritten>>> write,
        Action<IReadOnlyList<StateSpan>, IReadOnlyList<TWritten?>> fold,
        Action<StateSpan, DbUpdateException>? refuse)
    {
        try
        {
            await AttemptAsync(spans);
            return;
        }
        catch (DbUpdateException ex) when (refuse is not null && StoreRefusal.ReasonFor(ex) is not null)
        {
            if (spans.Count == 1)
            {
                refuse(spans[0], ex);
                return;
            }
        }

        foreach (var span in spans)
        {
            try
            {
                await AttemptAsync([span]);
            }
            catch (DbUpdateException refused) when (StoreRefusal.ReasonFor(refused) is not null)
            {
                refuse!(span, refused);
            }
        }

        async Task AttemptAsync(List<StateSpan> these)
        {
            IReadOnlyList<TWritten> written;
            try
            {
                written = await write(these);
            }
            catch (Exception ex) when (CommittedWrite.TryGet<IReadOnlyList<TWritten>>(ex, out var committed))
            {
                fold(these, [.. committed]);
                throw;
            }

            fold(these, [.. written]);
        }
    }
}
