using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for user food favorites.
/// </summary>
public class UserFoodFavoriteRepository : IUserFoodFavoriteRepository
{
    private readonly INocturneDbContextFactory _contexts;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserFoodFavoriteRepository"/> class.
    /// </summary>
    public UserFoodFavoriteRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <summary>
    /// Get favorite food entities for a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of favorite foods.</returns>
    public async Task<IReadOnlyList<Food>> GetFavoriteFoodsAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entities = await db
            .Set<UserFoodFavoriteEntity>()
            .AsNoTracking()
            .Where(f => f.UserId == userId)
            .Include(f => f.Food)
            .OrderBy(f => f.Food!.Name)
            .Select(f => f.Food!)
            .ToListAsync(cancellationToken);

        return entities.Select(FoodMapper.ToDomainModel).ToList();
    }

    /// <summary>
    /// Check if a food is a favorite for the user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="foodId">The unique identifier of the food.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the food is a favorite, otherwise false.</returns>
    public async Task<bool> IsFavoriteAsync(
        string userId,
        Guid foodId,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        return await db
            .Set<UserFoodFavoriteEntity>()
            .AsNoTracking()
            .AnyAsync(f => f.UserId == userId && f.FoodId == foodId, cancellationToken);
    }

    /// <summary>
    /// Add a favorite entry for a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="foodId">The unique identifier of the food to favorite.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The added favorite record, or null if it already exists.</returns>
    public async Task<UserFoodFavorite?> AddFavoriteAsync(
        string userId,
        Guid foodId,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var exists = await db
            .Set<UserFoodFavoriteEntity>()
            .AsNoTracking()
            .AnyAsync(f => f.UserId == userId && f.FoodId == foodId, cancellationToken);
        if (exists)
        {
            return null;
        }

        var entity = new UserFoodFavoriteEntity { UserId = userId, FoodId = foodId };

        db.Set<UserFoodFavoriteEntity>().Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return new UserFoodFavorite
        {
            Id = entity.Id,
            UserId = entity.UserId,
            FoodId = entity.FoodId,
        };
    }

    /// <summary>
    /// Remove a favorite entry for a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="foodId">The unique identifier of the food to unfavorite.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the favorite was removed, otherwise false.</returns>
    public async Task<bool> RemoveFavoriteAsync(
        string userId,
        Guid foodId,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await db
            .Set<UserFoodFavoriteEntity>()
            .FirstOrDefaultAsync(f => f.UserId == userId && f.FoodId == foodId, cancellationToken);

        if (entity == null)
        {
            return false;
        }

        db.Set<UserFoodFavoriteEntity>().Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
