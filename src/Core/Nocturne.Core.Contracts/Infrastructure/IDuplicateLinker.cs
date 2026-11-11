using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Infrastructure;

/// <summary>
/// Links newly written records to the duplicate groups of the records that describe the same event
/// from another source, or to groups of their own.
/// </summary>
public interface IDuplicateLinker
{
    /// <summary>
    /// Finds or creates the duplicate group of each of <paramref name="records"/> and links it there.
    /// A record already linked is left as it is.
    /// </summary>
    Task<DeduplicationBatchResult> DeduplicateBatchAsync(
        RecordType recordType,
        IReadOnlyList<DeduplicationInput> records,
        CancellationToken cancellationToken = default);
}
