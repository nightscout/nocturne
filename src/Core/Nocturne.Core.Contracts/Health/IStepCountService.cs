using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Health;

/// <summary>
/// Domain service for step count record operations with WebSocket broadcasting
/// </summary>
public interface IStepCountService
{
    /// <summary>
    /// Get step count records with optional pagination, newest first
    /// </summary>
    /// <param name="count">Maximum number of records to return</param>
    /// <param name="skip">Number of records to skip for pagination</param>
    /// <param name="from">Inclusive lower bound on the timestamp, or <see langword="null"/> for none</param>
    /// <param name="to">Inclusive upper bound on the timestamp, or <see langword="null"/> for none</param>
    Task<IEnumerable<StepCount>> GetStepCountsAsync(
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
    /// <returns>Collection of step count records ordered by timestamp ascending</returns>
    Task<IEnumerable<StepCount>> GetStepCountsByDateRangeAsync(
        DateTime from,
        DateTime to,
        int? count = null,
        int skip = 0,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Returns the latest step count timestamp written by <paramref name="source"/> (a connector's
    /// resume watermark), or <c>null</c> when that source has written none.
    /// </summary>
    /// <param name="source">The data source to scope to.</param>
    Task<DateTime?> GetLatestTimestampAsync(
        string source,
        CancellationToken cancellationToken = default
    );

    /// <returns>Step count record if found, null otherwise</returns>
    Task<StepCount?> GetStepCountByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create new step count records with WebSocket broadcasting
    /// </summary>
    /// <returns>Created records with assigned IDs</returns>
    Task<IEnumerable<StepCount>> CreateStepCountsAsync(
        IEnumerable<StepCount> stepCounts,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update an existing step count record with WebSocket broadcasting
    /// </summary>
    /// <param name="stepCount">Updated step count data</param>
    /// <returns>Updated record if successful, null otherwise</returns>
    Task<StepCount?> UpdateStepCountAsync(
        string id,
        StepCount stepCount,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a step count record with WebSocket broadcasting
    /// </summary>
    /// <returns>True if deleted successfully, false otherwise</returns>
    Task<bool> DeleteStepCountAsync(string id, CancellationToken cancellationToken = default);
}
