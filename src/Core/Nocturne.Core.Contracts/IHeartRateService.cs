using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Domain service for heart rate record operations with WebSocket broadcasting
/// </summary>
public interface IHeartRateService
{
    /// <summary>
    /// Get heart rate records with optional pagination
    /// </summary>
    /// <param name="count">Maximum number of records to return</param>
    /// <param name="skip">Number of records to skip for pagination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of heart rate records</returns>
    Task<IEnumerable<HeartRate>> GetHeartRatesAsync(
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get a specific heart rate record by ID
    /// </summary>
    /// <param name="id">Record ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Heart rate record if found, null otherwise</returns>
    Task<HeartRate?> GetHeartRateByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create new heart rate records with WebSocket broadcasting
    /// </summary>
    /// <param name="heartRates">Heart rate records to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created records with assigned IDs</returns>
    Task<IEnumerable<HeartRate>> CreateHeartRatesAsync(
        IEnumerable<HeartRate> heartRates,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update an existing heart rate record with WebSocket broadcasting
    /// </summary>
    /// <param name="id">Record ID to update</param>
    /// <param name="heartRate">Updated heart rate data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated record if successful, null otherwise</returns>
    Task<HeartRate?> UpdateHeartRateAsync(
        string id,
        HeartRate heartRate,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a heart rate record with WebSocket broadcasting
    /// </summary>
    /// <param name="id">Record ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted successfully, false otherwise</returns>
    Task<bool> DeleteHeartRateAsync(string id, CancellationToken cancellationToken = default);
}
