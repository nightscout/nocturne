using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Repositories;

/// <summary>
/// Repository port for <see cref="UserFoodFavorite"/> records linking users to their preferred foods.
/// </summary>
/// <seealso cref="UserFoodFavorite"/>
/// <seealso cref="Food"/>
/// <seealso cref="IFoodRepository"/>
public interface IUserFoodFavoriteRepository
{
    Task<IReadOnlyList<Food>> GetFavoriteFoodsAsync(
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a food is a favorite for the user.
    /// </summary>
    Task<bool> IsFavoriteAsync(
        string userId,
        Guid foodId,
        CancellationToken cancellationToken = default);

    Task<UserFoodFavorite?> AddFavoriteAsync(
        string userId,
        Guid foodId,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveFavoriteAsync(
        string userId,
        Guid foodId,
        CancellationToken cancellationToken = default);
}
