using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// Domain service for <see cref="Food"/> operations with WebSocket broadcasting.
/// </summary>
/// <seealso cref="Food"/>
public interface IFoodService
{
    /// <summary>
    /// Get a page of food records, ordered by name
    /// </summary>
    /// <param name="find">Optional text matched against name, category, and subcategory</param>
    /// <param name="count">Maximum number of records to return</param>
    /// <param name="skip">Number of records to skip for pagination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of food records</returns>
    Task<IEnumerable<Food>> GetFoodAsync(
        string? find = null,
        int count = 100,
        int skip = 0,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get a specific food record by ID
    /// </summary>
    /// <param name="id">Food ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Food record if found, null otherwise</returns>
    Task<Food?> GetFoodByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create new food records with WebSocket broadcasting
    /// </summary>
    /// <param name="foods">Food records to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created food records with assigned IDs</returns>
    Task<IEnumerable<Food>> CreateFoodAsync(
        IEnumerable<Food> foods,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update an existing food record with WebSocket broadcasting
    /// </summary>
    /// <param name="id">Food ID to update</param>
    /// <param name="food">Updated food data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated food record if successful, null otherwise</returns>
    Task<Food?> UpdateFoodAsync(
        string id,
        Food food,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a food record with WebSocket broadcasting
    /// </summary>
    /// <param name="id">Food ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted successfully, false otherwise</returns>
    Task<bool> DeleteFoodAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete multiple food records with optional filtering
    /// </summary>
    /// <param name="find">Optional MongoDB query filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of records deleted</returns>
    Task<long> DeleteMultipleFoodAsync(
        string? find = null,
        CancellationToken cancellationToken = default
    );
}
