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
    /// <returns>Collection of all food entries</returns>
    Task<IEnumerable<Food>> GetFoodAsync(CancellationToken cancellationToken = default);

    /// <returns>The food entry if found, null otherwise</returns>
    Task<Food?> GetFoodByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <param name="type">The food type to filter by</param>
    /// <returns>Collection of food entries of the specified type</returns>
    Task<IEnumerable<Food>> GetFoodByTypeAsync(
        string type,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get food entries with advanced filtering options including type filter
    /// </summary>
    /// <param name="count">Maximum number of food entries to return</param>
    /// <param name="skip">Number of food entries to skip</param>
    /// <param name="findQuery">Optional legacy find, read through <see cref="Nocturne.Core.Models.Queries.FindQuery"/>.</param>
    /// <param name="type">Optional food type filter</param>
    /// <param name="reverseResults">Whether to reverse the order of results</param>
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
    Task<ModifiedSincePage<Food>> GetFoodModifiedSinceAsync(
        long cursorMills,
        int limit,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create multiple food entries
    /// </summary>
    /// <param name="foods">The food entries to create</param>
    /// <returns>Collection of created food entries</returns>
    Task<IEnumerable<Food>> CreateFoodAsync(
        IEnumerable<Food> foods,
        CancellationToken cancellationToken = default
    );

    /// <param name="food">The updated food data</param>
    /// <returns>The updated food entry, or null if not found</returns>
    Task<Food?> UpdateFoodAsync(
        string id,
        Food food,
        CancellationToken cancellationToken = default
    );

    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteFoodAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the food entries a legacy <c>find</c> selects.
    /// </summary>
    /// <param name="find">The find, in either wire form. It must carry a condition.</param>
    /// <exception cref="ArgumentException">
    /// The find has no condition, or names a field or operator the food collection cannot filter on.
    /// </exception>
    /// <returns>Number of food entries deleted</returns>
    Task<long> BulkDeleteFoodAsync(string? find, CancellationToken cancellationToken = default);

    /// <summary>
    /// Count food entries with optional filtering including type filter
    /// </summary>
    /// <param name="findQuery">Optional legacy find, read through <see cref="Nocturne.Core.Models.Queries.FindQuery"/>.</param>
    /// <param name="type">Optional food type filter</param>
    /// <returns>Number of food entries matching the filter</returns>
    Task<long> CountFoodAsync(
        string? findQuery = null,
        string? type = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Foods whose name, category or subcategory contains <paramref name="text"/>, ordered by name.
    /// This is the V4 free-text search, distinct from the legacy <c>find</c> syntax.
    /// </summary>
    /// <param name="text">The text to look for; null or empty matches every food.</param>
    Task<IEnumerable<Food>> SearchFoodAsync(
        string? text,
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    );
}
