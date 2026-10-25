using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for Food operations
/// </summary>
public class FoodRepository : IFoodRepository
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly ILogger<FoodRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the FoodRepository class
    /// </summary>
    /// <param name="logger">The logger</param>
    public FoodRepository(INocturneDbContextFactory contexts, ILogger<FoodRepository> logger)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _logger = logger;
    }

    /// <summary>
    /// Get all food entries
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of all food entries.</returns>
    public async Task<IEnumerable<Food>> GetFoodAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entities = await db.Foods.AsNoTracking().OrderBy(f => f.Name).ToListAsync(cancellationToken);

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
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await FindEntityAsync(db.Foods.AsNoTracking(), id, cancellationToken);

        return entity != null ? FoodMapper.ToDomainModel(entity) : null;
    }

    /// <summary>
    /// The food <paramref name="id"/> names: its legacy ObjectId, its own UUID, or the id v3 serves
    /// for it (<see cref="MongoObjectId.Coerce"/>), which is the UUID's 24-hex prefix, or a hash of a
    /// legacy ObjectId stored in uppercase.
    /// </summary>
    private static async Task<FoodEntity?> FindEntityAsync(
        IQueryable<FoodEntity> foods,
        string id,
        CancellationToken cancellationToken
    )
    {
        var entity = await foods.FirstOrDefaultAsync(f => f.OriginalId == id, cancellationToken);

        if (entity == null && Guid.TryParse(id, out var guid))
            entity = await foods.FirstOrDefaultAsync(f => f.Id == guid, cancellationToken);

        if (entity != null || !MongoObjectId.TryGetGuidPrefixRange(id, out var low, out var high))
            return entity;

        entity = await foods
            .Where(f => f.Id >= low && f.Id <= high)
            .OrderBy(f => f.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (entity != null)
            return entity;

        var hashed = (await foods
                .Where(f => f.OriginalId != null && f.OriginalId != f.OriginalId.ToLower())
                .Select(f => new { f.Id, f.OriginalId })
                .ToListAsync(cancellationToken))
            .FirstOrDefault(f => MongoObjectId.Coerce(f.OriginalId) == id);

        return hashed == null
            ? null
            : await foods.FirstOrDefaultAsync(f => f.Id == hashed.Id, cancellationToken);
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
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entities = await db
            .Foods.AsNoTracking()
            .Where(f => f.Type == type)
            .OrderBy(f => f.Name)
            .ToListAsync(cancellationToken);

        return entities.Select(FoodMapper.ToDomainModel);
    }

    /// <summary>
    /// Get food entries with advanced filtering including type filter
    /// </summary>
    /// <param name="count">The maximum number of entries to return.</param>
    /// <param name="skip">The number of entries to skip.</param>
    /// <param name="findQuery">Optional legacy find, read through <see cref="FindQuery"/>.</param>
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
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var query = db.Foods.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(type))
        {
            query = query.Where(f => f.Type == type);
        }

        var find = ParseFind(findQuery);

        query = reverseResults ? query.OrderByDescending(f => f.Name) : query.OrderBy(f => f.Name);

        if (find.IsEmpty)
        {
            var page = await query.Skip(skip).Take(count).ToListAsync(cancellationToken);
            return page.Select(FoodMapper.ToDomainModel);
        }

        var matching = (await query.ToListAsync(cancellationToken))
            .Select(FoodMapper.ToDomainModel)
            .Where(f => find.Matches(f));

        return matching.Skip(skip).Take(count).ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Pages on <c>sys_updated_at</c> through <see cref="HistoryPage"/>, so an edited food is
    /// delivered again and a poll reads only the page, off <c>ix_foods_tenant_sys_updated_at</c>.
    /// A deleted food is delivered with <c>isValid: false</c>.
    /// </remarks>
    public async Task<ModifiedSincePage<Food>> GetFoodModifiedSinceAsync(
        long cursorMills,
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entities = await HistoryPage.GetAsync(
            db,
            db.Foods.IncludingDeleted().AsNoTracking(),
            f => f.SysUpdatedAt,
            f => f.Id,
            cursorMills,
            limit,
            _logger,
            "foods",
            cancellationToken
        );

        return new ModifiedSincePage<Food>(
            entities.Select(ToHistoryFood).ToList(),
            entities.Count > 0 ? entities[^1].SysUpdatedAt.ToMills() : null
        );
    }

    private static Food ToHistoryFood(FoodEntity entity)
    {
        var food = FoodMapper.ToDomainModel(entity);
        if (entity.DeletedAt is not null)
            food.IsValid = false;
        return food;
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
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var resultEntities = new List<FoodEntity>();

        foreach (var food in foods)
        {
            var entity = FoodMapper.ToEntity(food);
            // A legacy Mongo _id addresses its row through OriginalId, not the derived key.
            var entityId = entity.Id;
            var originalId = entity.OriginalId;
            // A deleted food keeps its row and key, so re-creating it restores that row, as a
            // Nightscout v3 create replaces the deleted document it matches.
            var existingEntity = await db.Foods.IncludingDeleted().FirstOrDefaultAsync(
                f => f.Id == entityId || (originalId != null && f.OriginalId == originalId),
                cancellationToken
            );

            if (existingEntity != null)
            {
                // Through the mapper rather than CurrentValues.SetValues: a row matched on its
                // OriginalId has its own primary key, and copying the derived one onto it throws.
                FoodMapper.UpdateEntity(existingEntity, food);
                existingEntity.DeletedAt = null;
                resultEntities.Add(existingEntity);
            }
            else
            {
                db.Foods.Add(entity);
                resultEntities.Add(entity);
            }
        }

        await db.SaveChangesAsync(cancellationToken);

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
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await FindEntityAsync(db.Foods, id, cancellationToken);

        if (entity == null)
        {
            return null;
        }

        FoodMapper.UpdateEntity(entity, food);
        await db.SaveChangesAsync(cancellationToken);

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
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await FindEntityAsync(db.Foods, id, cancellationToken);

        if (entity == null)
        {
            return false;
        }

        await SoftDeleteAsync(db, [entity], cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public async Task<long> BulkDeleteFoodAsync(
        string? find,
        CancellationToken cancellationToken = default
    )
    {
        var query = ParseFind(find);
        if (query.IsEmpty)
            throw new ArgumentException("A bulk delete requires a find with a condition.", nameof(find));

        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entities = (await db.Foods.ToListAsync(cancellationToken))
            .Where(f => query.Matches(FoodMapper.ToDomainModel(f)))
            .ToList();

        if (entities.Count > 0)
        {
            await SoftDeleteAsync(db, entities, cancellationToken);
        }

        return entities.Count;
    }

    private static FindQuery ParseFind(string? find)
    {
        var query = FindQuery.Parse(find);
        if (Food.FindRefusal(query) is { } refusal)
            throw new ArgumentException(refusal, nameof(find));

        return query;
    }

    /// <summary>
    /// Soft-deletes <paramref name="entities"/>, and does to the rows referencing them what the
    /// foreign keys do on a hard delete: meal attributions keep their portion as "Other", connector
    /// food entries lose the link, favorites go. <c>MutationAuditInterceptor</c> attributes the
    /// delete, which decides whether a connector re-import may restore the food.
    /// </summary>
    private static Task SoftDeleteAsync(NocturneDbContext db, List<FoodEntity> entities, CancellationToken cancellationToken)
    {
        var ids = entities.Select(f => f.Id).ToList();

        return db.ExecuteInTransactionAsync(async ct =>
        {
            await db.TreatmentFoods
                .Where(tf => tf.FoodId != null && ids.Contains(tf.FoodId.Value))
                .ExecuteUpdateAsync(s => s.SetProperty(tf => tf.FoodId, (Guid?)null), ct);
            await db.ConnectorFoodEntries
                .Where(e => e.FoodId != null && ids.Contains(e.FoodId.Value))
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.FoodId, (Guid?)null), ct);
            await db.UserFoodFavorites
                .Where(f => ids.Contains(f.FoodId))
                .ExecuteDeleteAsync(ct);

            var deletedAt = NocturneDbContext.UtcNowAtStoredPrecision();
            foreach (var entity in entities)
                entity.DeletedAt = deletedAt;

            return await db.SaveChangesAsync(ct);
        }, ct: cancellationToken);
    }

    /// <summary>
    /// Count food entries with type filter
    /// </summary>
    /// <param name="findQuery">Optional legacy find, read through <see cref="FindQuery"/>.</param>
    /// <param name="type">Optional food type filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The total number of matching food entries.</returns>
    public async Task<long> CountFoodAsync(
        string? findQuery = null,
        string? type = null,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var query = db.Foods.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(type))
        {
            query = query.Where(f => f.Type == type);
        }

        var find = ParseFind(findQuery);
        if (find.IsEmpty)
            return await query.CountAsync(cancellationToken);

        return (await query.ToListAsync(cancellationToken))
            .Select(FoodMapper.ToDomainModel)
            .LongCount(f => find.Matches(f));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Food>> SearchFoodAsync(
        string? text,
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var query = db.Foods.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(text))
        {
            query = query.Where(f =>
                f.Name.Contains(text)
                || f.Category.Contains(text)
                || f.Subcategory.Contains(text)
            );
        }

        var entities = await query
            .OrderBy(f => f.Name)
            .Skip(skip)
            .Take(count)
            .ToListAsync(cancellationToken);

        return entities.Select(FoodMapper.ToDomainModel);
    }
}
