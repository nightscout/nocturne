using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Connectors.Core.Interfaces;

public interface ITreatmentPublisher
{
    /// <remarks>
    /// Rows <paramref name="source"/> stored under a treatment's client id (<see cref="TreatmentClientId"/>)
    /// are moved onto the treatment's own id before the write, unless that id is already stored.
    /// </remarks>
    Task<bool> PublishTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishBolusesAsync(
        IEnumerable<Bolus> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishCarbIntakesAsync(
        IEnumerable<CarbIntake> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishBGChecksAsync(
        IEnumerable<BGCheck> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishBolusCalculationsAsync(
        IEnumerable<BolusCalculation> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishTempBasalsAsync(
        IEnumerable<TempBasal> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishBasalInjectionsAsync(
        IEnumerable<BasalInjection> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<DateTime?> GetLatestTreatmentTimestampAsync(
        string source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The newest record <paramref name="source"/> has stored of the one treatment type
    /// <paramref name="type"/> lands as, or <c>null</c> when it has stored none. Unlike
    /// <see cref="GetLatestTreatmentTimestampAsync(string, CancellationToken)"/>, which answers
    /// for every treatment type at once, a sibling type's newer record cannot stand in for it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> is not a treatment type.</exception>
    Task<DateTime?> GetLatestTreatmentTimestampAsync(
        SyncDataType type,
        string source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The legacy ids of the treatment records <paramref name="source"/> has stored with an event
    /// time in [<paramref name="from"/>, <paramref name="to"/>], each with that event time.
    /// </summary>
    Task<IReadOnlyDictionary<string, DateTime>> GetStoredTreatmentIdsAsync(
        string source,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes treatments a source may already have delivered: one never stored is created, and
    /// one the source has changed since it was stored is written again. Nothing the user deleted
    /// comes back.
    /// </summary>
    /// <returns>How many were written, or null when the write failed.</returns>
    Task<int?> PublishRecentTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        string source,
        WriteOrigin origin,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes <paramref name="source"/>'s treatment records under <paramref name="legacyIds"/>
    /// as a system sweep, so the source can publish them again should they reappear.
    /// </summary>
    Task<int> DeleteTreatmentsAsync(
        string source,
        IReadOnlySet<string> legacyIds,
        CancellationToken cancellationToken = default);
}
