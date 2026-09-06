using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// Domain service for user food favourites and recently used foods. Manages the per-user
/// favourites list and derives a recent-foods list from <see cref="TreatmentFood"/> records.
/// </summary>
/// <seealso cref="IUserFoodFavoriteService"/>
public class UserFoodFavoriteService : IUserFoodFavoriteService
{
    private readonly IUserFoodFavoriteRepository _favoriteRepository;
    private readonly ITreatmentFoodRepository _treatmentFoodRepository;
    private readonly ILogger<UserFoodFavoriteService> _logger;

    public UserFoodFavoriteService(
        IUserFoodFavoriteRepository favoriteRepository,
        ITreatmentFoodRepository treatmentFoodRepository,
        ILogger<UserFoodFavoriteService> logger
    )
    {
        _favoriteRepository = favoriteRepository;
        _treatmentFoodRepository = treatmentFoodRepository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Food>> GetFavoritesAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        return await _favoriteRepository.GetFavoriteFoodsAsync(userId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> AddFavoriteAsync(
        string userId,
        Guid foodId,
        CancellationToken cancellationToken = default
    )
    {
        var created = await _favoriteRepository.AddFavoriteAsync(userId, foodId, cancellationToken);
        _logger.LogDebug(
            "Add favorite for user {UserId} and food {FoodId}: {Created}",
            userId,
            foodId,
            created != null
        );
        return created != null;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveFavoriteAsync(
        string userId,
        Guid foodId,
        CancellationToken cancellationToken = default
    )
    {
        var removed = await _favoriteRepository.RemoveFavoriteAsync(userId, foodId, cancellationToken);
        _logger.LogDebug(
            "Remove favorite for user {UserId} and food {FoodId}: {Removed}",
            userId,
            foodId,
            removed
        );
        return removed;
    }

    /// <inheritdoc />
    public async Task<bool> IsFavoriteAsync(
        string userId,
        Guid foodId,
        CancellationToken cancellationToken = default
    )
    {
        return await _favoriteRepository.IsFavoriteAsync(userId, foodId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Food>> GetRecentFoodsAsync(
        string? userId,
        int limit = 20,
        CancellationToken cancellationToken = default
    )
    {
        var favoriteIds = new HashSet<string?>();
        if (!string.IsNullOrEmpty(userId))
        {
            var favorites = await _favoriteRepository.GetFavoriteFoodsAsync(
                userId,
                cancellationToken
            );
            favoriteIds = favorites.Select(f => f.Id).ToHashSet();
        }

        var recentCandidates = await _treatmentFoodRepository.GetRecentFoodsAsync(
            limit + favoriteIds.Count,
            cancellationToken
        );

        var recents = recentCandidates
            .Where(food => !favoriteIds.Contains(food.Id))
            .Take(limit)
            .ToList();

        return recents;
    }
}
