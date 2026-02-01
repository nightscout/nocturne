using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Services;

/// <summary>
/// Service for importing connector food entries and deduplicating foods.
/// </summary>
public class ConnectorFoodEntryService : IConnectorFoodEntryService
{
    private readonly NocturneDbContext _context;
    private readonly IMealMatchingService _mealMatchingService;
    private readonly ILogger<ConnectorFoodEntryService> _logger;

    public ConnectorFoodEntryService(
        NocturneDbContext context,
        IMealMatchingService mealMatchingService,
        ILogger<ConnectorFoodEntryService> logger
    )
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _mealMatchingService = mealMatchingService ?? throw new ArgumentNullException(nameof(mealMatchingService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<ConnectorFoodEntry>> ImportAsync(
        string userId,
        IEnumerable<ConnectorFoodEntryImport> imports,
        CancellationToken cancellationToken = default
    )
    {
        var importList = imports?.ToList() ?? new List<ConnectorFoodEntryImport>();
        if (importList.Count == 0)
        {
            return Array.Empty<ConnectorFoodEntry>();
        }

        var results = new List<ConnectorFoodEntry>(importList.Count);

        // Track foods added within this batch to prevent duplicate insertions
        // Key: "{connectorSource}:{externalFoodId}"
        var batchFoodCache = new Dictionary<string, FoodEntity>(StringComparer.OrdinalIgnoreCase);

        foreach (var import in importList)
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

                // Check batch cache first (foods added in this batch but not yet saved)
                if (batchFoodCache.TryGetValue(foodCacheKey, out var cachedFood))
                {
                    foodEntity = cachedFood;
                    UpdateFoodEntity(foodEntity, import.Food);
                }
                else
                {
                    // Check database
                    foodEntity = await _context.Foods.FirstOrDefaultAsync(
                        f => f.ExternalSource == connectorSource && f.ExternalId == foodExternalId,
                        cancellationToken
                    );

                    if (foodEntity == null)
                    {
                        foodEntity = BuildFoodEntity(import.Food, connectorSource);
                        _context.Foods.Add(foodEntity);
                        batchFoodCache[foodCacheKey] = foodEntity;
                    }
                    else
                    {
                        UpdateFoodEntity(foodEntity, import.Food);
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

                // Check batch cache first
                if (batchFoodCache.TryGetValue(foodCacheKey, out var cachedFood))
                {
                    foodEntity = cachedFood;
                }
                else
                {
                    foodEntity = await _context.Foods.FirstOrDefaultAsync(
                        f => f.ExternalSource == connectorSource && f.ExternalId == externalFoodId,
                        cancellationToken
                    );
                }
            }

            var entryEntity = await _context.ConnectorFoodEntries.FirstOrDefaultAsync(
                e => e.ConnectorSource == connectorSource && e.ExternalEntryId == externalEntryId,
                cancellationToken
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

                _context.ConnectorFoodEntries.Add(entryEntity);
            }
            else
            {
                entryEntity.ExternalFoodId = externalFoodId;
                entryEntity.FoodId = foodEntity?.Id ?? entryEntity.FoodId;
                entryEntity.ConsumedAt = import.ConsumedAt;
                entryEntity.LoggedAt = import.LoggedAt;
                entryEntity.MealName = import.MealName ?? entryEntity.MealName;
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
            return Array.Empty<ConnectorFoodEntry>();
        }

        await _context.SaveChangesAsync(cancellationToken);

        // Process new entries for meal matching
        var newEntryIds = results
            .Where(r => r.Status == ConnectorFoodEntryStatus.Pending)
            .Select(r => r.Id)
            .ToList();

        if (newEntryIds.Count > 0)
        {
            try
            {
                await _mealMatchingService.ProcessNewFoodEntriesAsync(userId, newEntryIds, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process food entries for meal matching");
                // Don't fail the import if matching fails
            }
        }

        return results;
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

    private static ConnectorFoodEntry MapToDomain(
        ConnectorFoodEntryEntity entity,
        FoodEntity? foodEntity
    )
    {
        return new ConnectorFoodEntry
        {
            Id = entity.Id,
            ConnectorSource = entity.ConnectorSource,
            ExternalEntryId = entity.ExternalEntryId,
            ExternalFoodId = entity.ExternalFoodId,
            FoodId = entity.FoodId,
            Food = foodEntity != null ? FoodMapper.ToDomainModel(foodEntity) : null,
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
            MatchedTreatmentId = entity.MatchedTreatmentId,
            ResolvedAt = entity.ResolvedAt,
        };
    }

    private static string TruncateUnit(string unit)
    {
        const int maxLength = 30;
        return unit.Length <= maxLength ? unit : unit[..maxLength];
    }
}
