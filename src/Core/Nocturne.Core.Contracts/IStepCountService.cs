using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Domain service for step count record operations with WebSocket broadcasting
/// </summary>
public interface IStepCountService
{
    /// <summary>
    /// Get step count records with optional pagination
    /// </summary>
    /// <param name="count">Maximum number of records to return</param>
    /// <param name="skip">Number of records to skip for pagination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of step count records</returns>
    Task<IEnumerable<StepCount>> GetStepCountsAsync(
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get a specific step count record by ID
    /// </summary>
    /// <param name="id">Record ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Step count record if found, null otherwise</returns>
    Task<StepCount?> GetStepCountByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create new step count records with WebSocket broadcasting
    /// </summary>
    /// <param name="stepCounts">Step count records to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created records with assigned IDs</returns>
    Task<IEnumerable<StepCount>> CreateStepCountsAsync(
        IEnumerable<StepCount> stepCounts,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update an existing step count record with WebSocket broadcasting
    /// </summary>
    /// <param name="id">Record ID to update</param>
    /// <param name="stepCount">Updated step count data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated record if successful, null otherwise</returns>
    Task<StepCount?> UpdateStepCountAsync(
        string id,
        StepCount stepCount,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a step count record with WebSocket broadcasting
    /// </summary>
    /// <param name="id">Record ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted successfully, false otherwise</returns>
    Task<bool> DeleteStepCountAsync(string id, CancellationToken cancellationToken = default);
}
