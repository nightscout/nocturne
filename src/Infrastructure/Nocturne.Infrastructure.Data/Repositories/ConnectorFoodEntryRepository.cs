using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for connector food entry operations
/// </summary>
public class ConnectorFoodEntryRepository : IConnectorFoodEntryRepository
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly ILogger<ConnectorFoodEntryRepository> _logger;
    private readonly ILegacyReadCacheInvalidation? _legacyReads;

    /// <inheritdoc cref="IConnectorFoodEntryRepository" />
    public ConnectorFoodEntryRepository(
        INocturneDbContextFactory contexts,
        ILogger<ConnectorFoodEntryRepository> logger,
        ILegacyReadCacheInvalidation? legacyReads = null)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _legacyReads = legacyReads;
    }

    /// <inheritdoc />
    public async Task<ConnectorFoodEntry?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entity = await db.ConnectorFoodEntries
            .Include(e => e.Food)
            .FirstOrDefaultAsync(e => e.Id == id, ct);

        return entity != null ? MapToDomain(entity) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConnectorFoodEntry>> GetPendingInTimeRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entities = await db.ConnectorFoodEntries
            .Include(e => e.Food)
            .Where(e => e.Status == ConnectorFoodEntryStatus.Pending)
            .Where(e => e.ConsumedAt >= from && e.ConsumedAt <= to)
            .OrderBy(e => e.ConsumedAt)
            .ToListAsync(ct);

        return entities.Select(MapToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConnectorFoodEntry>> GetByIdsAsync(
        IEnumerable<Guid> ids,
        CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var idList = ids.ToList();
        var entities = await db.ConnectorFoodEntries
            .Include(e => e.Food)
            .Where(e => idList.Contains(e.Id))
            .ToListAsync(ct);

        return entities.Select(MapToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task UpdateStatusAsync(
        Guid id,
        ConnectorFoodEntryStatus status,
        CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entity = await db.ConnectorFoodEntries
            .FirstOrDefaultAsync(e => e.Id == id, ct);

        if (entity != null)
        {
            entity.Status = status;
            entity.ResolvedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <inheritdoc />
    public async Task<ConnectorFoodEntryUpsert> UpsertAsync(
        IReadOnlyList<ConnectorFoodEntryImport> imports,
        CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var results = new List<ConnectorFoodEntry>(imports.Count);

        // Matching sees an entry only when there is something new to match on: created, restored
        // from a withdrawal, or its consumed time moved. Connectors re-read their whole lookback
        // window every cycle, so handing over every still-pending row re-matches and re-notifies
        // all of them every cycle.
        var idsForMatching = new HashSet<Guid>();

        var batchFoodCache = new Dictionary<string, FoodEntity>(StringComparer.OrdinalIgnoreCase);

        foreach (var import in imports)
        {
            if (string.IsNullOrWhiteSpace(import.ConnectorSource))
            {
                _logger.LogWarning("Skipping connector food import with missing connector source");
                continue;
            }

            if (string.IsNullOrWhiteSpace(import.ExternalEntryId))
            {
                _logger.LogWarning("Skipping connector food import with missing external entry id");
                continue;
            }

            var connectorSource = import.ConnectorSource.Trim();
            var externalEntryId = import.ExternalEntryId.Trim();
            var externalFoodId = import.ExternalFoodId?.Trim() ?? string.Empty;

            FoodEntity? foodEntity = null;
            if (import.Food != null && !string.IsNullOrWhiteSpace(import.Food.ExternalId))
            {
                var foodExternalId = import.Food.ExternalId.Trim();
                var foodCacheKey = $"{connectorSource}:{foodExternalId}";

                if (batchFoodCache.TryGetValue(foodCacheKey, out var cachedFood))
                {
                    foodEntity = cachedFood;
                    UpdateFoodEntity(foodEntity, import.Food);
                }
                else
                {
                    // A deleted food keeps its external key. The user's delete stands, as it
                    // does for every other re-imported record; a system sweep's is undone on the
                    // same row.
                    var stored = db.Foods.IncludingDeleted().Where(
                        f => f.ExternalSource == connectorSource && f.ExternalId == foodExternalId);
                    var blocking = await stored.WhereBlocksRecreation().FirstOrDefaultAsync(ct);

                    if (blocking is { DeletedAt: not null })
                    {
                        foodEntity = null;
                    }
                    else if ((blocking ?? await stored.FirstOrDefaultAsync(ct)) is { } existing)
                    {
                        foodEntity = existing;
                        UpdateFoodEntity(foodEntity, import.Food);
                        foodEntity.DeletedAt = null;
                    }
                    else
                    {
                        foodEntity = BuildFoodEntity(import.Food, connectorSource);
                        db.Foods.Add(foodEntity);
                        batchFoodCache[foodCacheKey] = foodEntity;
                    }
                }

                if (string.IsNullOrWhiteSpace(externalFoodId))
                {
                    externalFoodId = foodExternalId;
                }
            }
            else if (!string.IsNullOrWhiteSpace(externalFoodId))
            {
                var foodCacheKey = $"{connectorSource}:{externalFoodId}";

                if (batchFoodCache.TryGetValue(foodCacheKey, out var cachedFood))
                {
                    foodEntity = cachedFood;
                }
                else
                {
                    foodEntity = await db.Foods.FirstOrDefaultAsync(
                        f => f.ExternalSource == connectorSource && f.ExternalId == externalFoodId,
                        ct
                    );
                }
            }

            var entryEntity = await db.ConnectorFoodEntries.FirstOrDefaultAsync(
                e => e.ConnectorSource == connectorSource && e.ExternalEntryId == externalEntryId,
                ct
            );

            if (entryEntity == null)
            {
                entryEntity = new ConnectorFoodEntryEntity
                {
                    Id = Guid.CreateVersion7(),
                    ConnectorSource = connectorSource,
                    ExternalEntryId = externalEntryId,
                    ExternalFoodId = externalFoodId,
                    FoodId = foodEntity?.Id,
                    ConsumedAt = import.ConsumedAt,
                    LoggedAt = import.LoggedAt,
                    MealName = import.MealName ?? string.Empty,
                    Carbs = import.Carbs,
                    Protein = import.Protein,
                    Fat = import.Fat,
                    Energy = import.Energy,
                    Servings = import.Servings,
                    ServingDescription = import.ServingDescription,
                    Status = ConnectorFoodEntryStatus.Pending,
                };

                db.ConnectorFoodEntries.Add(entryEntity);
                idsForMatching.Add(entryEntity.Id);
            }
            else
            {
                entryEntity.ExternalFoodId = externalFoodId;
                entryEntity.FoodId = foodEntity?.Id ?? entryEntity.FoodId;

                // The connector reporting the entry is evidence it exists. Nothing else returns a
                // record to Pending, so a withdrawal that proves wrong is otherwise permanent.
                if (entryEntity.Status == ConnectorFoodEntryStatus.Deleted)
                {
                    entryEntity.Status = ConnectorFoodEntryStatus.Pending;
                    entryEntity.ResolvedAt = null;
                    idsForMatching.Add(entryEntity.Id);

                    _logger.LogInformation(
                        "Restored withdrawn food entry {FoodEntryId} after {ConnectorSource} reported it again",
                        entryEntity.Id,
                        connectorSource);
                }

                // An inferred time is re-derived every sync and can come back worse: MyFitnessPal
                // recovers the meal name per cycle, so a cycle that fails to would rewrite an 08:00
                // breakfast to an unnamed midday and move its carbs onto a different bolus. Only
                // that case is refused; a reported time, or an inferred one that names its meal,
                // still writes.
                var wouldReplaceNamedMealWithGuess =
                    import.IsTimeInferred
                    && string.IsNullOrWhiteSpace(import.MealName)
                    && !string.IsNullOrWhiteSpace(entryEntity.MealName);

                if (!wouldReplaceNamedMealWithGuess)
                {
                    // Matching keys off the consumed time, and a corrected one arrives as an update
                    // rather than a create, so this is the only place the change is observable.
                    if (entryEntity.ConsumedAt != import.ConsumedAt
                        && entryEntity.Status == ConnectorFoodEntryStatus.Pending)
                    {
                        idsForMatching.Add(entryEntity.Id);
                    }

                    entryEntity.ConsumedAt = import.ConsumedAt;
                    entryEntity.LoggedAt = import.LoggedAt;
                    entryEntity.MealName = import.MealName ?? entryEntity.MealName;
                }

                entryEntity.Carbs = import.Carbs;
                entryEntity.Protein = import.Protein;
                entryEntity.Fat = import.Fat;
                entryEntity.Energy = import.Energy;
                entryEntity.Servings = import.Servings;
                entryEntity.ServingDescription = import.ServingDescription;
            }

            results.Add(MapToDomain(entryEntity, foodEntity));
        }

        if (results.Count == 0)
        {
            return new ConnectorFoodEntryUpsert([], idsForMatching);
        }

        var foodsChanged = db.ChangeTracker.Entries<FoodEntity>().Any(e => e.State == EntityState.Modified);

        await db.SaveChangesAsync(ct);

        if (foodsChanged && _legacyReads is not null)
        {
            await _legacyReads.WrittenAsync([typeof(Food)]);
        }

        return new ConnectorFoodEntryUpsert(results, idsForMatching);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> MarkMissingAsDeletedAsync(
        string connectorSource,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlySet<string> presentExternalEntryIds,
        DateTimeOffset resolvedAt,
        CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);

        // Only entries still awaiting a decision are withdrawn. A matched or dismissed one has been
        // acted on and its carbs are linked to a treatment. Pending is also the only status that
        // produces match suggestions.
        var candidates = await db.ConnectorFoodEntries
            .Where(e => e.ConnectorSource == connectorSource
                        && e.Status == ConnectorFoodEntryStatus.Pending
                        && e.ConsumedAt >= from
                        && e.ConsumedAt <= to)
            .ToListAsync(ct);

        var removed = candidates.Where(e => !presentExternalEntryIds.Contains(e.ExternalEntryId)).ToList();
        if (removed.Count == 0)
        {
            return [];
        }

        foreach (var entry in removed)
        {
            entry.Status = ConnectorFoodEntryStatus.Deleted;
            entry.ResolvedAt = resolvedAt;
        }

        await db.SaveChangesAsync(ct);

        return removed.ConvertAll(e => e.Id);
    }

    private static FoodEntity BuildFoodEntity(ConnectorFoodImport food, string connectorSource)
    {
        return new FoodEntity
        {
            Id = Guid.CreateVersion7(),
            Type = "food",
            Name = food.Name,
            Category = food.BrandName ?? string.Empty,
            Subcategory = string.Empty,
            Portion = (double)food.Portion,
            Unit = string.IsNullOrWhiteSpace(food.Unit) ? "g" : TruncateUnit(food.Unit),
            Carbs = (double)food.Carbs,
            Protein = (double)food.Protein,
            Fat = (double)food.Fat,
            Energy = (double)food.Energy,
            ExternalSource = connectorSource,
            ExternalId = food.ExternalId,
            Gi = GlycemicIndex.Medium,
        };
    }

    private static void UpdateFoodEntity(FoodEntity entity, ConnectorFoodImport food)
    {
        entity.Name = food.Name;
        entity.Category = food.BrandName ?? string.Empty;
        entity.Portion = (double)food.Portion;
        entity.Unit = string.IsNullOrWhiteSpace(food.Unit) ? entity.Unit : TruncateUnit(food.Unit);
        entity.Carbs = (double)food.Carbs;
        entity.Protein = (double)food.Protein;
        entity.Fat = (double)food.Fat;
        entity.Energy = (double)food.Energy;
    }

    private static string TruncateUnit(string unit)
    {
        const int maxLength = 30;
        return unit.Length <= maxLength ? unit : unit[..maxLength];
    }

    private static ConnectorFoodEntry MapToDomain(ConnectorFoodEntryEntity entity) =>
        MapToDomain(entity, entity.Food);

    private static ConnectorFoodEntry MapToDomain(ConnectorFoodEntryEntity entity, FoodEntity? food)
    {
        return new ConnectorFoodEntry
        {
            Id = entity.Id,
            ConnectorSource = entity.ConnectorSource,
            ExternalEntryId = entity.ExternalEntryId,
            ExternalFoodId = entity.ExternalFoodId,
            FoodId = entity.FoodId,
            Food = food != null ? FoodMapper.ToDomainModel(food) : null,
            ConsumedAt = entity.ConsumedAt,
            LoggedAt = entity.LoggedAt,
            MealName = entity.MealName,
            Carbs = entity.Carbs,
            Protein = entity.Protein,
            Fat = entity.Fat,
            Energy = entity.Energy,
            Servings = entity.Servings,
            ServingDescription = entity.ServingDescription,
            Status = entity.Status,
            ResolvedAt = entity.ResolvedAt,
        };
    }
}
