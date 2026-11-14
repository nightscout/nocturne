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
    /// <param name="mode">The write rules. V1 and V3 pass <see cref="FoodWriteMode.Legacy"/>.</param>
    /// <remarks>A <see cref="FoodWriteMode.Native"/> batch skips a food whose name sanitizes to empty and creates the rest.</remarks>
    /// <returns>Created food records with assigned IDs</returns>
    Task<IEnumerable<Food>> CreateFoodAsync(
        IEnumerable<Food> foods,
        FoodWriteMode mode,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create one food record under the <see cref="FoodWriteMode.Native"/> rules.
    /// </summary>
    /// <exception cref="FoodNameRequiredException">The name sanitizes to empty.</exception>
    Task<Food> CreateNativeFoodAsync(Food food, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update an existing food record with WebSocket broadcasting
    /// </summary>
    /// <param name="food">Updated food data</param>
    /// <param name="mode">The write rules. V1 and V3 pass <see cref="FoodWriteMode.Legacy"/>.</param>
    /// <exception cref="FoodNameRequiredException">A <see cref="FoodWriteMode.Native"/> write leaves the name empty.</exception>
    /// <returns>Updated food record if successful, null otherwise</returns>
    Task<Food?> UpdateFoodAsync(
        string id,
        Food food,
        FoodWriteMode mode,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a food record with WebSocket broadcasting
    /// </summary>
    /// <returns>True if deleted successfully, false otherwise</returns>
    Task<bool> DeleteFoodAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete the food records a legacy <c>find</c> selects, with WebSocket broadcasting
    /// </summary>
    /// <param name="find">The find, in either wire form. It must carry a condition.</param>
    /// <exception cref="ArgumentException">The find has no condition, or names a field or operator the food collection cannot filter on.</exception>
    /// <returns>Number of food records deleted</returns>
    Task<long> BulkDeleteFoodAsync(string? find, CancellationToken cancellationToken = default);
}
