using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Persists resolved device attribution for one <see cref="IDeviceAttributed"/> record type.
/// Split from <see cref="IDeviceAttributedRepository{TRecord}"/> so back-stamping can write through
/// a repository whose unattributed backlog is read with type-specific filters (device events).
/// </summary>
public interface IDeviceAttributionWriter
{
    /// <summary>
    /// Persists <see cref="IDeviceAttributed.PatientDeviceId"/> for the given record ids in one batch.
    /// Ids absent for this tenant are ignored.
    /// </summary>
    /// <returns>The number of rows updated.</returns>
    Task<int> SetPatientDeviceIdsAsync(
        IReadOnlyDictionary<Guid, Guid> patientDeviceIdByRecordId, CancellationToken ct = default);
}

/// <summary>
/// Reads and writes the unattributed backlog of one <see cref="IDeviceAttributed"/> record type,
/// so a device registration can back-stamp the history it explains.
/// </summary>
/// <typeparam name="TRecord">The device-attributed record type.</typeparam>
public interface IDeviceAttributedRepository<TRecord> : IDeviceAttributionWriter
    where TRecord : IDeviceAttributed
{
    /// <summary>
    /// Returns unattributed records (<c>PatientDeviceId == null</c>) within the time window,
    /// newest first, capped at <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<TRecord>> GetUnattributedAsync(
        DateTime? from, DateTime? to, int limit, CancellationToken ct = default);
}
