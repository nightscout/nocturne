using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="BasalSchedule"/> records representing a named scheduled basal rate program.
/// </summary>
/// <remarks>
/// One of the five siblings a legacy Nightscout profile upload decomposes into; the profile-scoped
/// lookups they share live on <see cref="IProfileScopedRepository{TRecord}"/>.
/// </remarks>
/// <seealso cref="BasalSchedule"/>
/// <seealso cref="IV4Repository{T}"/>
public interface IBasalScheduleRepository : IProfileScopedRepository<BasalSchedule>
{
    /// <summary>Retrieve a page of <see cref="BasalSchedule"/> records filtered by time range, device, and source.</summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">Maximum number of records to return (default 100).</param>
    /// <param name="offset">Number of records to skip for pagination (default 0).</param>
    /// <param name="descending">When <c>true</c>, results are ordered newest-first (default).</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<IEnumerable<BasalSchedule>> GetAsync(
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
