using System.Collections;
using System.Runtime.CompilerServices;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// The records a bulk write returns, and how many of the submitted records it did not write because the
/// user had deleted them. A repository returns what it persisted; a service returns what it returned
/// before it carried the count.
/// </summary>
/// <remarks>
/// A collection expression builds one with nothing skipped.
/// </remarks>
[CollectionBuilder(typeof(BulkWrite), nameof(BulkWrite.Create))]
public sealed class BulkWrite<TRecord>(IReadOnlyList<TRecord> written, int skippedDeleted) : IReadOnlyList<TRecord>
{
    /// <summary>
    /// Records the caller asked to write that were not written because the user had deleted them,
    /// counted once the batch is collapsed to one record per identity. A re-import never brings those
    /// back.
    /// </summary>
    public int SkippedDeleted { get; } = skippedDeleted;

    /// <summary>
    /// The written records that updated a stored row in place rather than inserting one. The same
    /// instances the collection enumerates.
    /// </summary>
    public IReadOnlyList<TRecord> Updated { get; init; } = [];

    /// <summary>
    /// The records whose stored row already held what they carry, so nothing was written for them.
    /// Disjoint from <see cref="Updated"/>; the same instances the collection enumerates.
    /// </summary>
    public IReadOnlyList<TRecord> Unchanged { get; init; } = [];

    /// <summary>The records that inserted a row: those neither <see cref="Updated"/> nor <see cref="Unchanged"/>.</summary>
    public IReadOnlyList<TRecord> Created
    {
        get
        {
            if (Updated.Count == 0 && Unchanged.Count == 0)
                return written;
            var stored = new HashSet<object?>(Updated.Concat(Unchanged).Cast<object?>(), ReferenceEqualityComparer.Instance);
            return written.Where(r => !stored.Contains(r)).ToList();
        }
    }

    /// <summary>
    /// Submitted records not written because writing them fails the same way every time, in
    /// submission order. The rest of the batch was still written.
    /// </summary>
    public IReadOnlyList<BulkWriteRejection<TRecord>> Rejected { get; init; } = [];

    public int Count => written.Count;

    public TRecord this[int index] => written[index];

    public IEnumerator<TRecord> GetEnumerator() => written.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A submitted record a bulk write refused, and why.</summary>
/// <param name="Index">Its zero-based position in the submitted batch.</param>
/// <param name="Record">The record as submitted.</param>
/// <param name="Reason">Why it cannot be stored, fit to show the client.</param>
public sealed record BulkWriteRejection<TRecord>(int Index, TRecord Record, string Reason);

/// <summary>Builds a <see cref="BulkWrite{TRecord}"/> from a collection expression.</summary>
public static class BulkWrite
{
    public static BulkWrite<TRecord> Create<TRecord>(ReadOnlySpan<TRecord> written) => new(written.ToArray(), 0);
}

/// <summary>
/// The outcome of <see cref="ILegacyKeyedRepository{TRecord}.BulkUpsertByLegacyIdAsync"/>: each
/// written record keyed by legacy id, and how many records were withheld because the user had
/// deleted them.
/// </summary>
/// <typeparam name="TRecord">The V4 record type.</typeparam>
public sealed record LegacyUpsertBatch<TRecord>(
    IReadOnlyDictionary<string, LegacyUpsert<TRecord>> Outcomes,
    int SkippedDeleted);
