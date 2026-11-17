using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="BGCheck"/> records representing fingerstick blood glucose meter readings.
/// </summary>
/// <remarks>
/// BG checks are used to supplement or calibrate continuous glucose monitor data.
/// </remarks>
/// <seealso cref="BGCheck"/>
/// <seealso cref="IV4Repository{T}"/>
public interface IBGCheckRepository : ILegacyKeyedRepository<BGCheck>, ISyncKeyedRepository<BGCheck>
{
    /// <summary>
    /// Retrieve a page of <see cref="BGCheck"/> records filtered by time range, device, source, and origin.
    /// </summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Inclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">Maximum number of records to return (default 100).</param>
    /// <param name="offset">Number of records to skip for pagination (default 0).</param>
    /// <param name="descending">When <c>true</c>, results are ordered newest-first (default).</param>
    new Task<IEnumerable<BGCheck>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        CancellationToken ct = default
    );

    /// <summary>
    /// Retrieve the timestamp of the most recently stored <see cref="BGCheck"/>, optionally scoped to a data source.
    /// </summary>
    /// <remarks>Used by connectors to resume per-source sync without re-fetching already-stored data.</remarks>
    /// <param name="source">Optional data source filter. Pass <c>null</c> to search across all sources.</param>
    Task<DateTime?> GetLatestTimestampAsync(string? source = null, CancellationToken ct = default);

    /// <summary>Retrieve all <see cref="BGCheck"/> records sharing the same correlation identifier.</summary>
    /// <param name="correlationId">Correlation ID linking related records.</param>
    Task<IEnumerable<BGCheck>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    );
}
