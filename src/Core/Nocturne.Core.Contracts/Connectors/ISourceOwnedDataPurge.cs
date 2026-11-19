using Nocturne.Core.Models.Connectors;

namespace Nocturne.Core.Contracts.Connectors;

/// <summary>
/// Deletes the rows a <see cref="RecordSelector"/> names outside the glucose tables, and releases the
/// re-import tombstones of every table including glucose, attributed to the scope's audit actor, as
/// the scope's reader.
/// </summary>
public interface ISourceOwnedDataPurge
{
    /// <summary>
    /// Soft-deletes every non-glucose row <paramref name="selector"/> names, stamping each as a user
    /// delete so no sync re-imports it, and records one bulk summary per table under the selector's
    /// <see cref="RecordSelector.Scope"/>.
    /// </summary>
    Task<SourcePurgeResult> SoftDeleteAsync(RecordSelector selector, CancellationToken ct);

    /// <summary>
    /// Releases every user tombstone of the rows <paramref name="selector"/> names, glucose included and
    /// those of earlier deletes too, so their sender can send them again. The rows stay deleted.
    /// </summary>
    Task AllowReimportAsync(RecordSelector selector, CancellationToken ct);
}
