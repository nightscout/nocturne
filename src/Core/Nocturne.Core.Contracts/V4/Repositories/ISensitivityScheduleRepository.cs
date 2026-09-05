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
public interface ISensitivityScheduleRepository : IProfileScopedRepository<SensitivitySchedule>
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
}
