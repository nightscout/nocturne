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
    Task<IEnumerable<Food>> GetFoodAsync(
        string? find = null,
        int count = 100,
        int skip = 0,
        CancellationToken cancellationToken = default
    );

    /// <returns>Food record if found, null otherwise</returns>
    Task<Food?> GetFoodByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create new food records with WebSocket broadcasting
    /// </summary>
    /// <returns>Created food records with assigned IDs</returns>
    Task<IEnumerable<Food>> CreateFoodAsync(
        IEnumerable<Food> foods,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update an existing food record with WebSocket broadcasting
    /// </summary>
    /// <param name="food">Updated food data</param>
    /// <returns>Updated food record if successful, null otherwise</returns>
    Task<Food?> UpdateFoodAsync(
        string id,
        Food food,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a food record with WebSocket broadcasting
    /// </summary>
    /// <returns>True if deleted successfully, false otherwise</returns>
    Task<bool> DeleteFoodAsync(string id, CancellationToken cancellationToken = default);
}
