using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="CarbRatioSchedule"/> records representing a named insulin-to-carb ratio program.
/// </summary>
/// <remarks>
/// Carb ratio schedules store the time-of-day-based grams-per-unit schedules associated with a
/// therapy settings profile. The profile-scoped lookups they share with the other decomposed
/// siblings live on <see cref="IProfileScopedRepository{TRecord}"/>.
/// </remarks>
/// <seealso cref="CarbRatioSchedule"/>
/// <seealso cref="IBasalScheduleRepository"/>
/// <seealso cref="ISensitivityScheduleRepository"/>
/// <seealso cref="IV4Repository{T}"/>
public interface ICarbRatioScheduleRepository : IProfileScopedRepository<CarbRatioSchedule>
{
    /// <summary>Retrieve a page of <see cref="CarbRatioSchedule"/> records filtered by time range, device, and source.</summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">Maximum number of records to return (default 100).</param>
    /// <param name="offset">Number of records to skip for pagination (default 0).</param>
    /// <param name="descending">When <c>true</c>, results are ordered newest-first (default).</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<IEnumerable<CarbRatioSchedule>> GetAsync(
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
