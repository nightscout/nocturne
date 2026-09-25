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
/// <typeparam name="TRecord">The record type written.</typeparam>
[CollectionBuilder(typeof(BulkWrite), nameof(BulkWrite.Create))]
public sealed class BulkWrite<TRecord>(IReadOnlyList<TRecord> written, int skippedDeleted) : IReadOnlyList<TRecord>
{
    /// <summary>
    /// Records the caller asked to write that were not written because the user had deleted them,
    /// counted once the batch is collapsed to one record per identity. A re-import never brings those
    /// back.
    /// </summary>
    public int SkippedDeleted { get; } = skippedDeleted;

    public int Count => written.Count;

    public TRecord this[int index] => written[index];

    public IEnumerator<TRecord> GetEnumerator() => written.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

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
