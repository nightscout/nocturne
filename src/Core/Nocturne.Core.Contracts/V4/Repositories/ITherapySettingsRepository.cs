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
public interface ITherapySettingsRepository : ILegacyKeyedRepository<TherapySettings>
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

    /// <summary>Retrieve all <see cref="TherapySettings"/> records for a named therapy profile.</summary>
    /// <param name="profileName">The profile name to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<TherapySettings>> GetByProfileNameAsync(string profileName, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent <see cref="TherapySettings"/> record for the given profile name
    /// that was active at-or-before the specified timestamp.
    /// </summary>
    /// <param name="profileName">The profile name to filter by.</param>
    /// <param name="timestamp">The point-in-time to query against.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TherapySettings?> GetActiveAtAsync(string profileName, DateTime timestamp, CancellationToken ct = default);

    /// <summary>
    /// Delete all <see cref="TherapySettings"/> records whose legacy ObjectId starts with <paramref name="prefix"/>.
    /// </summary>
    /// <remarks>Used during profile decomposition to replace an entire profile upload atomically.</remarks>
    /// <param name="prefix">Legacy ObjectId prefix to match.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteByLegacyIdPrefixAsync(string prefix, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>Retrieve all <see cref="TherapySettings"/> records sharing the same correlation identifier.</summary>
    /// <param name="correlationId">Correlation ID linking related records (e.g., from one profile upload).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<TherapySettings>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    );
}
