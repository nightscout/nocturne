using Nocturne.Core.Models.Connectors;

namespace Nocturne.Core.Contracts.Connectors;

/// <summary>
/// Deletes the rows a data source owns outside the glucose tables, and releases the re-import
/// tombstones of every table including glucose, attributed to the scope's audit actor, as the
/// scope's reader.
/// </summary>
public interface ISourceOwnedDataPurge
{
    /// <summary>
    /// Soft-deletes every non-glucose row <paramref name="source"/> owns, stamping each as a user
    /// delete so no sync re-imports it, and records one bulk summary per table for
    /// <paramref name="scope"/>.
    /// </summary>
    Task<SourcePurgeResult> SoftDeleteBySourceAsync(string source, string scope, CancellationToken ct);

    /// <summary>
    /// Releases every user tombstone <paramref name="source"/> produced, glucose included and those of
    /// earlier deletes too, so the source can send its records again. The rows stay deleted.
    /// </summary>
    Task AllowReimportBySourceAsync(string source, string scope, CancellationToken ct);
}
