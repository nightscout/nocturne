using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for food breakdown operations linked to carb intake records.
/// </summary>
public class TreatmentFoodRepository : ITreatmentFoodRepository
{
    private readonly INocturneDbContextFactory _contexts;

    /// <summary>
    /// Initializes a new instance of the <see cref="TreatmentFoodRepository"/> class.
    /// </summary>
    public TreatmentFoodRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <summary>
    /// Get food breakdown entries for a carb intake record.
    /// </summary>
    /// <param name="carbIntakeId">The unique identifier of the carb intake record.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of food breakdown entries.</returns>
    public async Task<IReadOnlyList<TreatmentFood>> GetByCarbIntakeIdAsync(
        Guid carbIntakeId,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entities = await db
            .Set<TreatmentFoodEntity>()
            .AsNoTracking()
            .Include(tf => tf.Food)
            .Where(tf => tf.CarbIntakeId == carbIntakeId)
            .OrderBy(tf => tf.SysCreatedAt)
            .ToListAsync(cancellationToken);

        return entities
            .Select(entity => TreatmentFoodMapper.ToDomainModel(entity, entity.Food))
            .ToList();
    }

    /// <summary>
    /// Get food breakdown entries for multiple carb intake records.
    /// </summary>
    /// <param name="carbIntakeIds">A collection of carb intake record identifiers.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of food breakdown entries matching the IDs.</returns>
    public async Task<IReadOnlyList<TreatmentFood>> GetByCarbIntakeIdsAsync(
        IEnumerable<Guid> carbIntakeIds,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var ids = carbIntakeIds.ToList();
        if (ids.Count == 0)
        {
            return Array.Empty<TreatmentFood>();
        }

        var entities = await db
            .Set<TreatmentFoodEntity>()
            .AsNoTracking()
            .Include(tf => tf.Food)
            .Where(tf => ids.Contains(tf.CarbIntakeId))
            .OrderBy(tf => tf.SysCreatedAt)
            .ToListAsync(cancellationToken);

        return entities
            .Select(entity => TreatmentFoodMapper.ToDomainModel(entity, entity.Food))
            .ToList();
    }

    /// <summary>
    /// Create a food breakdown entry.
    /// </summary>
    /// <param name="entry">The food breakdown entry to create.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created food breakdown entry.</returns>
    public async Task<TreatmentFood> CreateAsync(
        TreatmentFood entry,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = TreatmentFoodMapper.ToEntity(entry);
        db.Set<TreatmentFoodEntity>().Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        var food = entity.FoodId.HasValue
            ? await db
                .Foods.AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == entity.FoodId.Value, cancellationToken)
            : null;

        return TreatmentFoodMapper.ToDomainModel(entity, food);
    }

    /// <summary>
    /// Update a food breakdown entry.
    /// </summary>
    /// <param name="entry">The food breakdown entry with updated data.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated food breakdown entry, or null if not found.</returns>
    public async Task<TreatmentFood?> UpdateAsync(
        TreatmentFood entry,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await db
            .Set<TreatmentFoodEntity>()
            .FirstOrDefaultAsync(tf => tf.Id == entry.Id, cancellationToken);

        if (entity == null)
        {
            return null;
        }

        TreatmentFoodMapper.UpdateEntity(entity, entry);
        await db.SaveChangesAsync(cancellationToken);

        var food = entity.FoodId.HasValue
            ? await db
                .Foods.AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == entity.FoodId.Value, cancellationToken)
            : null;

        return TreatmentFoodMapper.ToDomainModel(entity, food);
    }

    /// <summary>
    /// Delete a food breakdown entry.
    /// </summary>
    /// <param name="id">The unique identifier of the entry to delete.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the entry was deleted, otherwise false.</returns>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await db
            .Set<TreatmentFoodEntity>()
            .FirstOrDefaultAsync(tf => tf.Id == id, cancellationToken);

        if (entity == null)
        {
            return false;
        }

        db.Set<TreatmentFoodEntity>().Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Count how many food attribution entries reference a specific food.
    /// </summary>
    /// <param name="foodId">The food unique identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The total number of referencing entries.</returns>
    public async Task<int> CountByFoodIdAsync(
        Guid foodId,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        return await db
            .Set<TreatmentFoodEntity>()
            .AsNoTracking()
            .CountAsync(tf => tf.FoodId == foodId, cancellationToken);
    }

    /// <summary>
    /// Clear food references for a specific food (set FoodId to null), keeping the attribution entries as "Other".
    /// </summary>
    /// <param name="foodId">The food unique identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of records updated.</returns>
    public async Task<int> ClearFoodReferencesByFoodIdAsync(
        Guid foodId,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        return await db
            .Set<TreatmentFoodEntity>()
            .Where(tf => tf.FoodId == foodId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(tf => tf.FoodId, (Guid?)null),
                cancellationToken
            );
    }

    /// <summary>
    /// Delete all food attribution entries that reference a specific food.
    /// </summary>
    /// <param name="foodId">The food unique identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of records deleted.</returns>
    public async Task<int> DeleteByFoodIdAsync(
        Guid foodId,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        return await db
            .Set<TreatmentFoodEntity>()
            .Where(tf => tf.FoodId == foodId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Get recently used foods ordered by last usage.
    /// </summary>
    /// <param name="limit">The maximum number of foods to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of recently used foods.</returns>
    public async Task<IReadOnlyList<Food>> GetRecentFoodsAsync(
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var recentFoodIds = await db
            .Set<TreatmentFoodEntity>()
            .AsNoTracking()
            .Where(tf => tf.FoodId != null)
            .GroupBy(tf => tf.FoodId)
            .Select(g => new { FoodId = g.Key!.Value, LastUsed = g.Max(tf => tf.SysCreatedAt) })
            .OrderByDescending(x => x.LastUsed)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var ids = recentFoodIds.Select(x => x.FoodId).ToList();

        var foods = await db.Foods
            .AsNoTracking()
            .Where(f => ids.Contains(f.Id))
            .ToListAsync(cancellationToken);

        var foodLookup = foods.ToDictionary(f => f.Id);
        return recentFoodIds
            .Where(x => foodLookup.ContainsKey(x.FoodId))
            .Select(x => FoodMapper.ToDomainModel(foodLookup[x.FoodId]))
            .ToList();
    }
}
