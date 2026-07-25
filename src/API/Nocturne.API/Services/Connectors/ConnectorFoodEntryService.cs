using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Services.Connectors;

/// <summary>
/// Imports food entries sourced from connectors (e.g. MyFitnessPal) and deduplicates them against
/// the existing food catalogue via <see cref="IMealMatchingService"/>. New food entries are created
/// for unmatched items; matched items are linked to the canonical food record.
/// </summary>
/// <seealso cref="IConnectorFoodEntryService"/>
public class ConnectorFoodEntryService : IConnectorFoodEntryService
{
    private readonly NocturneDbContext _context;
    private readonly IMealMatchingService _mealMatchingService;
    private readonly IInAppNotificationService _notificationService;
    private readonly ILogger<ConnectorFoodEntryService> _logger;

    public ConnectorFoodEntryService(
        NocturneDbContext context,
        IMealMatchingService mealMatchingService,
        IInAppNotificationService notificationService,
        ILogger<ConnectorFoodEntryService> logger
    )
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _mealMatchingService = mealMatchingService ?? throw new ArgumentNullException(nameof(mealMatchingService));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<ConnectorFoodEntry>> ImportAsync(
        string? userId,
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

        // Only entries created by this call are handed to meal matching. Connectors re-read their
        // whole lookback window every cycle, so the rows that merely came back unchanged are
        // already-seen ones; re-processing them would re-run matching and re-notify every cycle.
        var createdIds = new List<Guid>();

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
                createdIds.Add(entryEntity.Id);
            }
            else
            {
                entryEntity.ExternalFoodId = externalFoodId;
                entryEntity.FoodId = foodEntity?.Id ?? entryEntity.FoodId;

                // An inferred time is a guess the connector re-derives every sync, and the guess can
                // get worse: MyFitnessPal recovers the meal name per cycle, so a transient failure
                // would otherwise rewrite a breakfast logged at 08:00 to an unnamed midday and move
                // its carbs onto a different bolus. Refuse only that downgrade — an inferred time
                // that does name its meal is an improvement on one that could not, and a reported
                // time always wins. Nutrition is reported directly, so it always refreshes.
                var wouldReplaceNamedMealWithGuess =
                    import.IsTimeInferred
                    && string.IsNullOrWhiteSpace(import.MealName)
                    && !string.IsNullOrWhiteSpace(entryEntity.MealName);

                if (!wouldReplaceNamedMealWithGuess)
                {
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
            return Array.Empty<ConnectorFoodEntry>();
        }

        await _context.SaveChangesAsync(cancellationToken);

        // Importing does not depend on having someone to notify, so a tenant without a resolvable
        // owner still gets its entries and its suggestion list; only the notifications are skipped.
        if (createdIds.Count > 0 && !string.IsNullOrEmpty(userId))
        {
            try
            {
                await _mealMatchingService.ProcessNewFoodEntriesAsync(userId, createdIds, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process food entries for meal matching");
                // Don't fail the import if matching fails
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<int> MarkMissingAsDeletedAsync(
        string userId,
        string connectorSource,
        DateTimeOffset from,
        DateTimeOffset to,
        IEnumerable<string> presentExternalEntryIds,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(connectorSource))
        {
            return 0;
        }

        var source = connectorSource.Trim();
        var present = presentExternalEntryIds?.ToHashSet(StringComparer.Ordinal)
                      ?? new HashSet<string>(StringComparer.Ordinal);

        // Only entries still awaiting a decision are withdrawn. One the user already matched or
        // dismissed has been acted on, and its carbs are linked to a treatment; removing it
        // underneath them would undo their own work. Pending is also the only status that keeps
        // producing match suggestions, which is what an upstream deletion needs to stop.
        var candidates = await _context.ConnectorFoodEntries
            .Where(e => e.ConnectorSource == source
                        && e.Status == ConnectorFoodEntryStatus.Pending
                        && e.ConsumedAt >= from
                        && e.ConsumedAt <= to)
            .ToListAsync(cancellationToken);

        var removed = candidates.Where(e => !present.Contains(e.ExternalEntryId)).ToList();
        if (removed.Count == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in removed)
        {
            entry.Status = ConnectorFoodEntryStatus.Deleted;
            entry.ResolvedAt = now;
        }

        await _context.SaveChangesAsync(cancellationToken);

        // The suggestion card outlives the entry it refers to unless it is withdrawn too.
        foreach (var entry in removed)
        {
            try
            {
                await _notificationService.ArchiveBySourceAsync(
                    userId,
                    "meal_matching.suggested_match",
                    entry.Id.ToString(),
                    NotificationArchiveReason.ConditionMet,
                    cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to archive the match notification for withdrawn food entry {FoodEntryId}",
                    entry.Id);
            }
        }

        _logger.LogInformation(
            "Marked {Count} {ConnectorSource} food entries deleted after they disappeared upstream",
            removed.Count,
            source);

        return removed.Count;
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
            ResolvedAt = entity.ResolvedAt,
        };
    }

    private static string TruncateUnit(string unit)
    {
        const int maxLength = 30;
        return unit.Length <= maxLength ? unit : unit[..maxLength];
    }
}
