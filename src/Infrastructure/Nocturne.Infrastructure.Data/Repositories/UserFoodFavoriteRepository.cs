using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for user food favorites.
/// </summary>
public class UserFoodFavoriteRepository
{
    private readonly NocturneDbContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserFoodFavoriteRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    public UserFoodFavoriteRepository(NocturneDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Get favorite food entities for a user.
    /// </summary>
    public async Task<IReadOnlyList<FoodEntity>> GetFavoriteFoodsAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        return await _context
            .Set<UserFoodFavoriteEntity>()
            .AsNoTracking()
            .Where(f => f.UserId == userId)
            .Include(f => f.Food)
            .OrderBy(f => f.Food!.Name)
            .Select(f => f.Food!)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Check if a food is a favorite for the user.
    /// </summary>
    public async Task<bool> IsFavoriteAsync(
        string userId,
        Guid foodId,
        CancellationToken cancellationToken = default
    )
    {
        return await _context
            .Set<UserFoodFavoriteEntity>()
            .AsNoTracking()
            .AnyAsync(f => f.UserId == userId && f.FoodId == foodId, cancellationToken);
    }

    /// <summary>
    /// Add a favorite entry for a user.
    /// </summary>
    public async Task<UserFoodFavoriteEntity?> AddFavoriteAsync(
        string userId,
        Guid foodId,
        CancellationToken cancellationToken = default
    )
    {
        var exists = await IsFavoriteAsync(userId, foodId, cancellationToken);
        if (exists)
        {
            return null;
        }

        var entity = new UserFoodFavoriteEntity { UserId = userId, FoodId = foodId };

        _context.Set<UserFoodFavoriteEntity>().Add(entity);
        await _context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    /// <summary>
    /// Remove a favorite entry for a user.
    /// </summary>
    public async Task<bool> RemoveFavoriteAsync(
        string userId,
        Guid foodId,
        CancellationToken cancellationToken = default
    )
    {
        var entity = await _context
            .Set<UserFoodFavoriteEntity>()
            .FirstOrDefaultAsync(f => f.UserId == userId && f.FoodId == foodId, cancellationToken);

        if (entity == null)
        {
            return false;
        }

        _context.Set<UserFoodFavoriteEntity>().Remove(entity);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
