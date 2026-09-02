using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="BolusCalculation"/> records that capture bolus wizard inputs and outputs.
/// </summary>
/// <remarks>
/// <see cref="BolusCalculation"/> records are produced by pump bolus wizards and store the
/// carb input, correction factor, and suggested dose. They are distinct from the
/// delivered <see cref="Bolus"/> record itself.
/// </remarks>
/// <seealso cref="BolusCalculation"/>
/// <seealso cref="IBolusRepository"/>
/// <seealso cref="IV4Repository{T}"/>
public interface IBolusCalculationRepository : ILegacyKeyedRepository<BolusCalculation>
{
    /// <summary>Retrieve a page of <see cref="BolusCalculation"/> records filtered by time range, device, and source.</summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">Maximum number of records to return (default 100).</param>
    /// <param name="offset">Number of records to skip for pagination (default 0).</param>
    /// <param name="descending">When <c>true</c>, results are ordered newest-first (default).</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<IEnumerable<BolusCalculation>> GetAsync(
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
    /// Retrieve the timestamp of the most recently stored <see cref="BolusCalculation"/>, optionally scoped to a data source.
    /// </summary>
    /// <remarks>Used by connectors to resume per-source sync without re-fetching already-stored data.</remarks>
    /// <param name="source">Optional data source filter. Pass <c>null</c> to search across all sources.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DateTime?> GetLatestTimestampAsync(string? source = null, CancellationToken ct = default);

    /// <summary>Retrieve all <see cref="BolusCalculation"/> records sharing the same correlation identifier.</summary>
    /// <param name="correlationId">Correlation ID linking related records (e.g., bolus + wizard).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<BolusCalculation>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    );
}
