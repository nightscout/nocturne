using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="SensitivitySchedule"/> records representing the named insulin sensitivity
/// factor (ISF) schedule for a therapy profile (mg/dL per unit, time-of-day based).
/// </summary>
/// <remarks>
/// Sensitivity schedules are decomposed from legacy Nightscout profile uploads alongside
/// <see cref="BasalSchedule"/>, <see cref="CarbRatioSchedule"/>, and <see cref="TargetRangeSchedule"/>.
/// </remarks>
/// <seealso cref="SensitivitySchedule"/>
/// <seealso cref="IBasalScheduleRepository"/>
/// <seealso cref="ICarbRatioScheduleRepository"/>
/// <seealso cref="ITargetRangeScheduleRepository"/>
/// <seealso cref="IV4Repository{T}"/>
public interface ISensitivityScheduleRepository : ILegacyKeyedRepository<SensitivitySchedule>
{
    /// <summary>Retrieve a page of <see cref="SensitivitySchedule"/> records filtered by time range, device, and source.</summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">Maximum number of records to return (default 100).</param>
    /// <param name="offset">Number of records to skip for pagination (default 0).</param>
    /// <param name="descending">When <c>true</c>, results are ordered newest-first (default).</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<IEnumerable<SensitivitySchedule>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        CancellationToken ct = default
    );

    /// <summary>Retrieve all <see cref="SensitivitySchedule"/> records belonging to a named therapy profile.</summary>
    /// <param name="profileName">The profile name to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<SensitivitySchedule>> GetByProfileNameAsync(string profileName, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent <see cref="SensitivitySchedule"/> record for the given profile name
    /// that was active at-or-before the specified timestamp.
    /// </summary>
    /// <param name="profileName">The profile name to filter by.</param>
    /// <param name="timestamp">The point-in-time to query against.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SensitivitySchedule?> GetActiveAtAsync(string profileName, DateTime timestamp, CancellationToken ct = default);

    /// <summary>
    /// Delete all <see cref="SensitivitySchedule"/> records whose legacy ObjectId starts with <paramref name="prefix"/>.
    /// </summary>
    /// <remarks>Used during profile decomposition to replace an entire profile upload atomically.</remarks>
    /// <param name="prefix">Legacy ObjectId prefix to match.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteByLegacyIdPrefixAsync(string prefix, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>Retrieve all <see cref="SensitivitySchedule"/> records sharing the same correlation identifier.</summary>
    /// <param name="correlationId">Correlation ID linking related records (e.g., from one profile upload).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<SensitivitySchedule>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    );
}
