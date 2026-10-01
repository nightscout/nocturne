using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;

namespace Nocturne.Core.Contracts.Repositories;

/// <summary>
/// Repository port for <see cref="Food"/> domain operations.
/// </summary>
/// <seealso cref="Food"/>
/// <seealso cref="ITreatmentFoodRepository"/>
/// <seealso cref="IUserFoodFavoriteRepository"/>
public interface IFoodRepository
{
    /// <summary>
    /// Get all food entries
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of all food entries</returns>
    Task<IEnumerable<Food>> GetFoodAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a food entry by its ID
    /// </summary>
    /// <param name="id">The food ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The food entry if found, null otherwise</returns>
    Task<Food?> GetFoodByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get food entries by type
    /// </summary>
    /// <param name="type">The food type to filter by</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of food entries of the specified type</returns>
    Task<IEnumerable<Food>> GetFoodByTypeAsync(
        string type,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get food entries with advanced filtering options
    /// </summary>
    /// <param name="count">Maximum number of food entries to return</param>
    /// <param name="skip">Number of food entries to skip</param>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="reverseResults">Whether to reverse the order of results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of food entries matching the filter</returns>
    Task<IEnumerable<Food>> GetFoodWithAdvancedFilterAsync(
        int count = 10,
        int skip = 0,
        string? findQuery = null,
        bool reverseResults = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get food entries with advanced filtering options including type filter
    /// </summary>
    /// <param name="count">Maximum number of food entries to return</param>
    /// <param name="skip">Number of food entries to skip</param>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="type">Optional food type filter</param>
    /// <param name="reverseResults">Whether to reverse the order of results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of food entries matching the filter</returns>
    Task<IEnumerable<Food>> GetFoodWithAdvancedFilterAsync(
        int count = 10,
        int skip = 0,
        string? findQuery = null,
        string? type = null,
        bool reverseResults = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Foods whose server write stamp falls after <paramref name="cursorMills"/>, oldest first, for
    /// the v3 <c>history/{lastModified}</c> endpoint. The page ends on a millisecond boundary, so it
    /// may exceed <paramref name="limit"/>.
    /// </summary>
    /// <param name="cursorMills">The client's cursor, in Unix milliseconds.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<ModifiedSincePage<Food>> GetFoodModifiedSinceAsync(
        long cursorMills,
        int limit,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create multiple food entries
    /// </summary>
    /// <param name="foods">The food entries to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of created food entries</returns>
    Task<IEnumerable<Food>> CreateFoodAsync(
        IEnumerable<Food> foods,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update a food entry by ID
    /// </summary>
    /// <param name="id">The food ID</param>
    /// <param name="food">The updated food data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated food entry, or null if not found</returns>
    Task<Food?> UpdateFoodAsync(
        string id,
        Food food,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a food entry by ID
    /// </summary>
    /// <param name="id">The food ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteFoodAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk delete food entries using query filters
    /// </summary>
    /// <param name="findQuery">Query filter for food entries to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of food entries deleted</returns>
    Task<long> BulkDeleteFoodAsync(string findQuery, CancellationToken cancellationToken = default);

    /// <summary>
    /// Count food entries with optional filtering
    /// </summary>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of food entries matching the filter</returns>
    Task<long> CountFoodAsync(
        string? findQuery = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Count food entries with optional filtering including type filter
    /// </summary>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="type">Optional food type filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of food entries matching the filter</returns>
    Task<long> CountFoodAsync(
        string? findQuery = null,
        string? type = null,
        CancellationToken cancellationToken = default
    );
}
