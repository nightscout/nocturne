using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for treatment food breakdown operations.
/// </summary>
public class TreatmentFoodRepository
{
    private readonly NocturneDbContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="TreatmentFoodRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    public TreatmentFoodRepository(NocturneDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Get food breakdown entries for a treatment.
    /// </summary>
    public async Task<IReadOnlyList<TreatmentFood>> GetByTreatmentIdAsync(
        Guid treatmentId,
        CancellationToken cancellationToken = default
    )
    {
        var entities = await _context
            .Set<TreatmentFoodEntity>()
            .AsNoTracking()
            .Include(tf => tf.Food)
            .Where(tf => tf.TreatmentId == treatmentId)
            .OrderBy(tf => tf.SysCreatedAt)
            .ToListAsync(cancellationToken);

        return entities
            .Select(entity => TreatmentFoodMapper.ToDomainModel(entity, entity.Food))
            .ToList();
    }

    /// <summary>
    /// Get food breakdown entries for multiple treatments.
    /// </summary>
    public async Task<IReadOnlyList<TreatmentFood>> GetByTreatmentIdsAsync(
        IEnumerable<Guid> treatmentIds,
        CancellationToken cancellationToken = default
    )
    {
        var ids = treatmentIds.ToList();
        if (ids.Count == 0)
        {
            return Array.Empty<TreatmentFood>();
        }

        var entities = await _context
            .Set<TreatmentFoodEntity>()
            .AsNoTracking()
            .Include(tf => tf.Food)
            .Where(tf => ids.Contains(tf.TreatmentId))
            .OrderBy(tf => tf.SysCreatedAt)
            .ToListAsync(cancellationToken);

        return entities
            .Select(entity => TreatmentFoodMapper.ToDomainModel(entity, entity.Food))
            .ToList();
    }

    /// <summary>
    /// Create a treatment food entry.
    /// </summary>
    public async Task<TreatmentFood> CreateAsync(
        TreatmentFood entry,
        CancellationToken cancellationToken = default
    )
    {
        var entity = TreatmentFoodMapper.ToEntity(entry);
        _context.Set<TreatmentFoodEntity>().Add(entity);
        await _context.SaveChangesAsync(cancellationToken);

        var food = entity.FoodId.HasValue
            ? await _context
                .Foods.AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == entity.FoodId.Value, cancellationToken)
            : null;

        return TreatmentFoodMapper.ToDomainModel(entity, food);
    }

    /// <summary>
    /// Update a treatment food entry.
    /// </summary>
    public async Task<TreatmentFood?> UpdateAsync(
        TreatmentFood entry,
        CancellationToken cancellationToken = default
    )
    {
        var entity = await _context
            .Set<TreatmentFoodEntity>()
            .FirstOrDefaultAsync(tf => tf.Id == entry.Id, cancellationToken);

        if (entity == null)
        {
            return null;
        }

        TreatmentFoodMapper.UpdateEntity(entity, entry);
        await _context.SaveChangesAsync(cancellationToken);

        var food = entity.FoodId.HasValue
            ? await _context
                .Foods.AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == entity.FoodId.Value, cancellationToken)
            : null;

        return TreatmentFoodMapper.ToDomainModel(entity, food);
    }

    /// <summary>
    /// Delete a treatment food entry.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _context
            .Set<TreatmentFoodEntity>()
            .FirstOrDefaultAsync(tf => tf.Id == id, cancellationToken);

        if (entity == null)
        {
            return false;
        }

        _context.Set<TreatmentFoodEntity>().Remove(entity);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Get recently used foods ordered by last usage.
    /// </summary>
    public async Task<IReadOnlyList<FoodEntity>> GetRecentFoodsAsync(
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        var recentFoods = await _context
            .Set<TreatmentFoodEntity>()
            .AsNoTracking()
            .Where(tf => tf.FoodId != null)
            .Join(
                _context.Foods.AsNoTracking(),
                tf => tf.FoodId,
                f => f.Id,
                (tf, f) => new { tf, f }
            )
            .GroupBy(x => x.f.Id)
            .Select(g => new { Food = g.First().f, LastUsed = g.Max(x => x.tf.SysCreatedAt) })
            .OrderByDescending(x => x.LastUsed)
            .Take(limit)
            .Select(x => x.Food)
            .ToListAsync(cancellationToken);

        return recentFoods;
    }
}
