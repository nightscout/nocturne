using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

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
    public async Task WriteAsync(
        IStateSpanService service, DecompositionResult result, Dictionary<StateSpan, Guid> owners, CancellationToken ct)
    {
        if (Changed.Count > 0)
        {
            IReadOnlyList<StateSpan?> updated;
            try
            {
                updated = await service.UpdateStateSpansAsync(Changed, ct);
            }
            catch (Exception ex) when (CommittedWrite.TryGet<IReadOnlyList<StateSpan?>>(ex, out var committed))
            {
                FoldUpdated(committed);
                throw;
            }

            FoldUpdated(updated);
        }

        if (Opened.Count > 0)
        {
            IReadOnlyList<StateSpanUpsert> upserted;
            try
            {
                upserted = await service.UpsertStateSpansAsync(Opened, ct);
            }
            catch (Exception ex) when (CommittedWrite.TryGet<IReadOnlyList<StateSpanUpsert>>(ex, out var committed))
            {
                FoldUpserted(committed);
                throw;
            }

            FoldUpserted(upserted);
        }

        void FoldUpdated(IReadOnlyList<StateSpan?> updated)
        {
            foreach (var (span, written) in Changed.Zip(updated))
            {
                if (written is null)
                    continue;
                if (owners.TryGetValue(span, out var owner))
                    owners[written] = owner;
                result.UpdatedRecords.Add(written);
            }
        }

        void FoldUpserted(IReadOnlyList<StateSpanUpsert> upserted)
        {
            foreach (var (span, (written, outcome)) in Opened.Zip(upserted))
            {
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
}
