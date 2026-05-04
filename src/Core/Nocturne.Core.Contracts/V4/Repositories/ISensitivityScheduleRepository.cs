using Nocturne.Core.Models.V4;

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
public interface ISensitivityScheduleRepository : IV4Repository<SensitivitySchedule>
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

    /// <summary>Returns a single <see cref="SensitivitySchedule"/> by its UUID v7, or <c>null</c> if not found.</summary>
    /// <param name="id">UUID v7 record identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<SensitivitySchedule?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Retrieve a <see cref="SensitivitySchedule"/> by its original MongoDB ObjectId.</summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching record, or <c>null</c> if not found.</returns>
    Task<SensitivitySchedule?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default);

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

    /// <summary>Persist a new <see cref="SensitivitySchedule"/> and return the saved entity.</summary>
    /// <param name="model">Record to create.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<SensitivitySchedule> CreateAsync(SensitivitySchedule model, CancellationToken ct = default);

    /// <summary>Replace an existing <see cref="SensitivitySchedule"/> identified by <paramref name="id"/>.</summary>
    /// <param name="id">UUID v7 identifier of the record to update.</param>
    /// <param name="model">Updated record data.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<SensitivitySchedule> UpdateAsync(Guid id, SensitivitySchedule model, CancellationToken ct = default);

    /// <summary>Delete a <see cref="SensitivitySchedule"/> by its UUID v7.</summary>
    /// <param name="id">UUID v7 identifier of the record to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Delete the <see cref="SensitivitySchedule"/> with the given legacy MongoDB ObjectId.</summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted (0 or 1).</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>
    /// Delete all <see cref="SensitivitySchedule"/> records whose legacy ObjectId starts with <paramref name="prefix"/>.
    /// </summary>
    /// <remarks>Used during profile decomposition to replace an entire profile upload atomically.</remarks>
    /// <param name="prefix">Legacy ObjectId prefix to match.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteByLegacyIdPrefixAsync(string prefix, CancellationToken ct = default);

    /// <summary>Count <see cref="SensitivitySchedule"/> records within an optional time range.</summary>
    /// <param name="from">Inclusive start, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end, or <c>null</c> for no upper bound.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default);

    /// <summary>Retrieve all <see cref="SensitivitySchedule"/> records sharing the same correlation identifier.</summary>
    /// <param name="correlationId">Correlation ID linking related records (e.g., from one profile upload).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<SensitivitySchedule>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    );

    /// <summary>Insert multiple <see cref="SensitivitySchedule"/> records in a single batch operation.</summary>
    /// <param name="records">Records to insert.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The inserted records with server-assigned fields populated.</returns>
    Task<IEnumerable<SensitivitySchedule>> BulkCreateAsync(
        IEnumerable<SensitivitySchedule> records,
        CancellationToken ct = default
    );
}
