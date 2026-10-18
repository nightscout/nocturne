using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Health;

/// <summary>
/// Domain service for heart rate record operations with WebSocket broadcasting
/// </summary>
public interface IHeartRateService
{
    /// <summary>
    /// Get heart rate records with optional pagination, newest first
    /// </summary>
    /// <param name="count">Maximum number of records to return</param>
    /// <param name="skip">Number of records to skip for pagination</param>
    /// <param name="from">Inclusive lower bound on the timestamp, or <see langword="null"/> for none</param>
    /// <param name="to">Inclusive upper bound on the timestamp, or <see langword="null"/> for none</param>
    Task<IEnumerable<HeartRate>> GetHeartRatesAsync(
        int count = 10,
        int skip = 0,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken cancellationToken = default
    );

    /// <param name="from">Start of date range (inclusive)</param>
    /// <param name="to">End of date range (exclusive)</param>
    /// <param name="count">Maximum number of records to return; <see langword="null"/> for every record in the range</param>
    /// <param name="skip">Number of records to skip for pagination</param>
    /// <returns>Collection of heart rate records ordered by timestamp ascending</returns>
    Task<IEnumerable<HeartRate>> GetHeartRatesByDateRangeAsync(
        DateTime from,
        DateTime to,
        int? count = null,
        int skip = 0,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get one averaged reading per UTC minute within a date range.
    /// </summary>
    /// <remarks>
    /// This is a presentation query for high-volume reports. It never changes or deletes the
    /// raw heart-rate records; callers that need the original measurements should use
    /// <see cref="GetHeartRatesByDateRangeAsync"/> instead.
    /// </remarks>
    /// <param name="from">Start of date range (inclusive)</param>
    /// <param name="to">End of date range (exclusive)</param>
    /// <returns>Chronologically ordered minute averages</returns>
    Task<IEnumerable<HeartRate>> GetHeartRateMinuteAveragesByDateRangeAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Returns the latest heart rate timestamp written by <paramref name="source"/> (a connector's
    /// resume watermark), or <c>null</c> when that source has written none.
    /// </summary>
    /// <param name="source">The data source to scope to.</param>
    Task<DateTime?> GetLatestTimestampAsync(
        string source,
        CancellationToken cancellationToken = default
    );

    /// <returns>Heart rate record if found, null otherwise</returns>
    Task<HeartRate?> GetHeartRateByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create new heart rate records with WebSocket broadcasting
    /// </summary>
    /// <returns>Created records with assigned IDs</returns>
    Task<IEnumerable<HeartRate>> CreateHeartRatesAsync(
        IEnumerable<HeartRate> heartRates,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update an existing heart rate record with WebSocket broadcasting
    /// </summary>
    /// <param name="heartRate">Updated heart rate data</param>
    /// <returns>Updated record if successful, null otherwise</returns>
    Task<HeartRate?> UpdateHeartRateAsync(
        string id,
        HeartRate heartRate,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a heart rate record with WebSocket broadcasting
    /// </summary>
    /// <returns>True if deleted successfully, false otherwise</returns>
    Task<bool> DeleteHeartRateAsync(string id, CancellationToken cancellationToken = default);
}
