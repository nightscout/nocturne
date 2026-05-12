using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for Food operations
/// </summary>
public class FoodRepository : IFoodRepository
{
    private readonly NocturneDbContext _context;

    /// <summary>
    /// Initializes a new instance of the FoodRepository class
    /// </summary>
    /// <param name="context">The database context</param>
    public FoodRepository(NocturneDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Get all food entries
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of all food entries.</returns>
    public async Task<IEnumerable<Food>> GetFoodAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _context.Foods.AsNoTracking().OrderBy(f => f.Name).ToListAsync(cancellationToken);

        return entities.Select(FoodMapper.ToDomainModel);
    }

    /// <summary>
    /// Get a specific food by ID
    /// </summary>
    /// <param name="id">The unique identifier (GUID or legacy string ID).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The food, or null if not found.</returns>
    public async Task<Food?> GetFoodByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        // Try to find by original ID first (MongoDB ObjectId)
        var entity = await _context.Foods.AsNoTracking().FirstOrDefaultAsync(
            f => f.OriginalId == id,
            cancellationToken
        );

        // If not found by original ID, try by GUID
        if (entity == null && Guid.TryParse(id, out var guid))
        {
            entity = await _context.Foods.AsNoTracking().FirstOrDefaultAsync(f => f.Id == guid, cancellationToken);
        }

        return entity != null ? FoodMapper.ToDomainModel(entity) : null;
    }

    /// <summary>
    /// Get food entries by type
    /// </summary>
    /// <param name="type">The type of food to filter by.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of food entries matching the type.</returns>
    public async Task<IEnumerable<Food>> GetFoodByTypeAsync(
        string type,
        CancellationToken cancellationToken = default
    )
    {
        var entities = await _context
            .Foods.AsNoTracking()
            .Where(f => f.Type == type)
            .OrderBy(f => f.Name)
            .ToListAsync(cancellationToken);

        return entities.Select(FoodMapper.ToDomainModel);
    }

    /// <summary>
    /// Get food entries with advanced filtering
    /// </summary>
    /// <param name="count">The maximum number of entries to return.</param>
    /// <param name="skip">The number of entries to skip.</param>
    /// <param name="findQuery">Optional search query string.</param>
    /// <param name="reverseResults">Whether to reverse the order of results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of matching food entries.</returns>
    public async Task<IEnumerable<Food>> GetFoodWithAdvancedFilterAsync(
        int count = 10,
        int skip = 0,
        string? findQuery = null,
        bool reverseResults = false,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.Foods.AsNoTracking().AsQueryable();

        // Apply find query filter if specified
        if (!string.IsNullOrEmpty(findQuery))
        {
            // Simple text search across name, category, and subcategory
            query = query.Where(f =>
                f.Name.Contains(findQuery)
                || f.Category.Contains(findQuery)
                || f.Subcategory.Contains(findQuery)
            );
        }

        // Apply ordering
        query = reverseResults ? query.OrderByDescending(f => f.Name) : query.OrderBy(f => f.Name);

        // Apply pagination
        var entities = await query.Skip(skip).Take(count).ToListAsync(cancellationToken);

        return entities.Select(FoodMapper.ToDomainModel);
    }

    /// <summary>
    /// Get food entries with advanced filtering including type filter
    /// </summary>
    /// <param name="count">The maximum number of entries to return.</param>
    /// <param name="skip">The number of entries to skip.</param>
    /// <param name="findQuery">Optional search query string.</param>
    /// <param name="type">Optional food type filter.</param>
    /// <param name="reverseResults">Whether to reverse the order of results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of matching food entries.</returns>
    public async Task<IEnumerable<Food>> GetFoodWithAdvancedFilterAsync(
        int count = 10,
        int skip = 0,
        string? findQuery = null,
        string? type = null,
        bool reverseResults = false,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.Foods.AsNoTracking().AsQueryable();

        // Apply type filter if specified
        if (!string.IsNullOrEmpty(type))
        {
            query = query.Where(f => f.Type == type);
        }

        // Apply find query filter if specified
        if (!string.IsNullOrEmpty(findQuery))
        {
            // Simple text search across name, category, and subcategory
            query = query.Where(f =>
                f.Name.Contains(findQuery)
                || f.Category.Contains(findQuery)
                || f.Subcategory.Contains(findQuery)
            );
        }

        // Apply ordering
        query = reverseResults ? query.OrderByDescending(f => f.Name) : query.OrderBy(f => f.Name);

        // Apply pagination
        var entities = await query.Skip(skip).Take(count).ToListAsync(cancellationToken);

        return entities.Select(FoodMapper.ToDomainModel);
    }

    /// <summary>
    /// Create multiple food entries
    /// </summary>
    /// <param name="foods">The collection of food entries to create.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of created food entries.</returns>
    public async Task<IEnumerable<Food>> CreateFoodAsync(
        IEnumerable<Food> foods,
        CancellationToken cancellationToken = default
    )
    {
        var entities = foods.Select(FoodMapper.ToEntity).ToList();
        var resultEntities = new List<FoodEntity>();

        foreach (var entity in entities)
        {
            // Check if a food with this ID already exists
            var existingEntity = await _context.Foods.FirstOrDefaultAsync(
                f => f.Id == entity.Id,
                cancellationToken
            );

            if (existingEntity != null)
            {
                var tenantId = existingEntity.TenantId;
                _context.Entry(existingEntity).CurrentValues.SetValues(entity);
                existingEntity.TenantId = tenantId;
                resultEntities.Add(existingEntity);
            }
            else
            {
                // Add new entity
                _context.Foods.Add(entity);
                resultEntities.Add(entity);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return resultEntities.Select(FoodMapper.ToDomainModel);
    }

    /// <summary>
    /// Update an existing food entry
    /// </summary>
    /// <param name="id">The unique identifier of the food to update.</param>
    /// <param name="food">The updated food data.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated food, or null if not found.</returns>
    public async Task<Food?> UpdateFoodAsync(
        string id,
        Food food,
        CancellationToken cancellationToken = default
    )
    {
        // Try to find by original ID first (MongoDB ObjectId)
        var entity = await _context.Foods.FirstOrDefaultAsync(
            f => f.OriginalId == id,
            cancellationToken
        );

        // If not found by original ID, try by GUID
        if (entity == null && Guid.TryParse(id, out var guid))
        {
            entity = await _context.Foods.FirstOrDefaultAsync(f => f.Id == guid, cancellationToken);
        }

        if (entity == null)
        {
            return null;
        }

        FoodMapper.UpdateEntity(entity, food);
        await _context.SaveChangesAsync(cancellationToken);

        return FoodMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Delete a food entry by ID
    /// </summary>
    /// <param name="id">The unique identifier of the food to delete.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the food was deleted, otherwise false.</returns>
    public async Task<bool> DeleteFoodAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        // Try to find by original ID first (MongoDB ObjectId)
        var entity = await _context.Foods.FirstOrDefaultAsync(
            f => f.OriginalId == id,
            cancellationToken
        );

        // If not found by original ID, try by GUID
        if (entity == null && Guid.TryParse(id, out var guid))
        {
            entity = await _context.Foods.FirstOrDefaultAsync(f => f.Id == guid, cancellationToken);
        }

        if (entity == null)
        {
            return false;
        }

        _context.Foods.Remove(entity);
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Bulk delete food entries with query
    /// </summary>
    /// <param name="findQuery">The search query for deletion.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<long> BulkDeleteFoodAsync(
        string findQuery,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.Foods.AsQueryable();

        // Apply find query filter
        if (!string.IsNullOrEmpty(findQuery))
        {
            // Simple text search across name, category, and subcategory
            query = query.Where(f =>
                f.Name.Contains(findQuery)
                || f.Category.Contains(findQuery)
                || f.Subcategory.Contains(findQuery)
            );
        }

        var entities = await query.ToListAsync(cancellationToken);
        var count = entities.Count;

        if (count > 0)
        {
            _context.Foods.RemoveRange(entities);
            await _context.SaveChangesAsync(cancellationToken);
        }

        return count;
    }

    /// <summary>
    /// Count food entries
    /// </summary>
    /// <param name="findQuery">Optional search query string.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The total number of matching food entries.</returns>
    public async Task<long> CountFoodAsync(
        string? findQuery = null,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.Foods.AsNoTracking().AsQueryable();

        // Apply find query filter if specified
        if (!string.IsNullOrEmpty(findQuery))
        {
            // Simple text search across name, category, and subcategory
            query = query.Where(f =>
                f.Name.Contains(findQuery)
                || f.Category.Contains(findQuery)
                || f.Subcategory.Contains(findQuery)
            );
        }

        return await query.CountAsync(cancellationToken);
    }

    /// <summary>
    /// Count food entries with type filter
    /// </summary>
    /// <param name="findQuery">Optional search query string.</param>
    /// <param name="type">Optional food type filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The total number of matching food entries.</returns>
    public async Task<long> CountFoodAsync(
        string? findQuery = null,
        string? type = null,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.Foods.AsNoTracking().AsQueryable();

        // Apply type filter if specified
        if (!string.IsNullOrEmpty(type))
        {
            query = query.Where(f => f.Type == type);
        }

        // Apply find query filter if specified
        if (!string.IsNullOrEmpty(findQuery))
        {
            // Simple text search across name, category, and subcategory
            query = query.Where(f =>
                f.Name.Contains(findQuery)
                || f.Category.Contains(findQuery)
                || f.Subcategory.Contains(findQuery)
            );
        }

        return await query.CountAsync(cancellationToken);
    }
}
