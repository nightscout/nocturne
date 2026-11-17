using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Events;

/// <summary>
/// Removes from an upstream the copies write-back sent of a treatment a user or an API client deleted.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="IDataEventSink{T}.OnDeletedAsync"/>, which also announces bulk deletes
/// one record at a time and carries no proof the record was ever written upstream. The caller raises
/// it once per single delete, naming only the records whose upstream copy may be deleted. A failure
/// upstream, or in reading the upstream's configuration, is logged and never reaches the caller; a
/// cancellation the caller asked for does.
/// </remarks>
public interface ITreatmentUpstreamDeletion
{
    /// <param name="deleted">The treatment as reads served it before the delete.</param>
    /// <param name="legacyIds">
    /// The legacy ids of the deleted records whose upstream copy may be deleted. Only the copies of
    /// these are removed, whatever legacy id <paramref name="deleted"/> carries.
    /// </param>
    /// <param name="ct">Cancels the operation.</param>
    Task DeleteUpstreamAsync(Treatment deleted, IReadOnlyCollection<string> legacyIds, CancellationToken ct = default);
}
