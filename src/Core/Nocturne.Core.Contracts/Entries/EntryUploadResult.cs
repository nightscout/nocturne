using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Entries;

/// <summary>
/// What <see cref="IEntryService.UpsertUploadAsync"/> wrote for a processed upload batch.
/// </summary>
/// <param name="Response">One element per processed entry, in order: the entry as written, or the stored row a re-send matched.</param>
/// <param name="GlucoseMoved">The entries whose glucose value was not stored before.</param>
public sealed record EntryUploadResult(IReadOnlyList<Entry> Response, IReadOnlyList<Entry> GlucoseMoved);
