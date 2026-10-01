using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="TherapySettings"/> records representing a versioned snapshot of all
/// therapy parameters (DIA, targets, schedules) for a named therapy profile.
/// </summary>
/// <remarks>
/// <see cref="TherapySettings"/> is a composite record that groups <see cref="BasalSchedule"/>,
/// <see cref="CarbRatioSchedule"/>, <see cref="SensitivitySchedule"/>, and <see cref="TargetRangeSchedule"/>
/// under a single profile name and effective-from timestamp. Used for historical therapy auditing
/// and for seeding new schedule records when a profile is edited.
/// </remarks>
/// <seealso cref="TherapySettings"/>
/// <seealso cref="IBasalScheduleRepository"/>
/// <seealso cref="ISensitivityScheduleRepository"/>
/// <seealso cref="IV4Repository{T}"/>
public interface ITherapySettingsRepository : IProfileScopedRepository<TherapySettings>
{
    /// <summary>Retrieve a page of <see cref="TherapySettings"/> records filtered by time range, device, and source.</summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">Maximum number of records to return (default 100).</param>
    /// <param name="offset">Number of records to skip for pagination (default 0).</param>
    /// <param name="descending">When <c>true</c>, results are ordered newest-first (default).</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<IEnumerable<TherapySettings>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        CancellationToken ct = default
    );

    /// <summary>The rows currently flagged <see cref="TherapySettings.IsDefault"/>, newest first.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<TherapySettings>> GetDefaultsAsync(CancellationToken ct = default);

    /// <summary>
    /// The newest row decomposed from a profile document, leaving out profile-switch snapshots
    /// (see <see cref="TherapySettings.ProfileSwitchStoreMarker"/>).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<TherapySettings?> GetNewestDocumentRowAsync(CancellationToken ct = default);

    /// <summary>
    /// Makes the row <paramref name="id"/> the tenant's only <see cref="TherapySettings.IsDefault"/> row,
    /// or clears the flag on every row when <paramref name="id"/> is <c>null</c>.
    /// </summary>
    /// <param name="id">The therapy settings row to flag, or <c>null</c> for none.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SetDefaultAsync(Guid? id, CancellationToken ct = default);
}
