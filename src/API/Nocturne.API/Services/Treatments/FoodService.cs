using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Models;
using Nocturne.Core.Contracts.Repositories;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// Domain service implementation for <see cref="Food"/> operations with WebSocket broadcasting.
/// Sanitizes food records via <see cref="IDocumentProcessingService"/> on creation and deduplicates
/// by a composite key built from all food properties.
/// </summary>
/// <seealso cref="IFoodService"/>
/// <seealso cref="IFoodRepository"/>
/// <seealso cref="IWriteSideEffects"/>
/// <seealso cref="IDocumentProcessingService"/>
public class FoodService : IFoodService
{
    private readonly IFoodRepository _food;
    private readonly IDocumentProcessingService _documentProcessingService;
    private readonly IWriteSideEffects _sideEffects;
    private readonly IDataEventSink<Food> _events;
    private readonly ILogger<FoodService> _logger;
    private const string CollectionName = "food";

    /// <summary>
    /// Initializes a new instance of <see cref="FoodService"/>.
    /// </summary>
    /// <param name="food">The food repository for data access.</param>
    /// <param name="documentProcessingService">Service for HTML sanitization of food fields.</param>
    /// <param name="sideEffects">Handles cache invalidation, SignalR broadcasting, and V4 decomposition on writes.</param>
    /// <param name="events">The event sink for broadcasting create/update/delete events.</param>
    /// <param name="logger">The logger instance.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is <see langword="null"/>.</exception>
    public FoodService(
        IFoodRepository food,
        IDocumentProcessingService documentProcessingService,
        IWriteSideEffects sideEffects,
        IDataEventSink<Food> events,
        ILogger<FoodService> logger
    )
    {
        _food =
            food ?? throw new ArgumentNullException(nameof(food));
        _documentProcessingService =
            documentProcessingService
            ?? throw new ArgumentNullException(nameof(documentProcessingService));
        _sideEffects =
            sideEffects
            ?? throw new ArgumentNullException(nameof(sideEffects));
        _events =
            events
            ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Food>> GetFoodAsync(
        string? find = null,
        int count = 100,
        int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug(
                "Getting food records with find: {Find}, count: {Count}, skip: {Skip}",
                find,
                count,
                skip
            );
            return await _food.GetFoodWithAdvancedFilterAsync(
                count,
                skip,
                find,
                reverseResults: false,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting food records");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Food?> GetFoodByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Getting food record by ID: {Id}", id);
            return await _food.GetFoodByIdAsync(id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting food record by ID: {Id}", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Food>> CreateFoodAsync(
        IEnumerable<Food> foods,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var foodList = foods.Where(f => f != null).ToList();
            _logger.LogDebug("Creating {Count} food records", foodList.Count);

            var processedFoods = ProcessFoodDocuments(foodList);
            var processedList = processedFoods.ToList();

            // Create in database
            var createdFoods = await _food.CreateFoodAsync(
                processedList,
                cancellationToken
            );
            var resultList = createdFoods.ToList();

            await _sideEffects.OnCreatedAsync(CollectionName, resultList, cancellationToken: cancellationToken);
            await _events.OnCreatedAsync(resultList, cancellationToken);

            _logger.LogDebug("Successfully created {Count} food records", resultList.Count);
            return resultList;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating food records");
            throw;
        }

        IEnumerable<Food> ProcessFoodDocuments(IEnumerable<Food> documents)
        {
            var processed = new List<Food>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var food in documents)
            {
                SanitizeFood(food);

                var dedupKey = BuildDedupKey(food);
                if (!seenKeys.Add(dedupKey))
                {
                    _logger.LogDebug("Skipping duplicate food entry with key {Key}", dedupKey);
                    continue;
                }

                processed.Add(food);
            }

            return processed;
        }

        void SanitizeFood(Food food)
        {
            food.Type = Sanitize(food.Type, "food");
            food.Category = Sanitize(food.Category);
            food.Subcategory = Sanitize(food.Subcategory);
            food.Name = Sanitize(food.Name);
            food.Unit = Sanitize(food.Unit, "g");

            if (food.Gi < 1 || food.Gi > 3)
            {
                food.Gi = 2;
            }

            if (food.Foods == null || food.Foods.Count == 0)
            {
                return;
            }

            foreach (var quickPick in food.Foods)
            {
                quickPick.Name = Sanitize(quickPick.Name);
                quickPick.Unit = Sanitize(quickPick.Unit, quickPick.Unit ?? string.Empty);
            }
        }

        string Sanitize(string? value, string defaultValue = "")
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            var sanitized = _documentProcessingService.SanitizeHtml(value);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return value.Trim();
            }

            return sanitized.Trim();
        }

        string BuildDedupKey(Food food)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(food.Id))
            {
                builder.Append(food.Id).Append('|');
            }

            builder
                .Append(food.Type)
                .Append('|')
                .Append(food.Name)
                .Append('|')
                .Append(food.Category)
                .Append('|')
                .Append(food.Subcategory)
                .Append('|')
                .Append(food.Unit)
                .Append('|')
                .Append(food.Portion.ToString(CultureInfo.InvariantCulture))
                .Append('|')
                .Append(food.Carbs.ToString(CultureInfo.InvariantCulture))
                .Append('|')
                .Append(food.Fat.ToString(CultureInfo.InvariantCulture))
                .Append('|')
                .Append(food.Protein.ToString(CultureInfo.InvariantCulture))
                .Append('|')
                .Append(food.Energy.ToString(CultureInfo.InvariantCulture))
                .Append('|')
                .Append(food.Gi)
                .Append('|')
                .Append(food.Position)
                .Append('|')
                .Append(food.HideAfterUse)
                .Append('|')
                .Append(food.Hidden);

            if (food.Foods?.Count > 0)
            {
                foreach (
                    var quickPick in food.Foods.OrderBy(
                        f => f.Name,
                        StringComparer.OrdinalIgnoreCase
                    )
                )
                {
                    builder
                        .Append('|')
                        .Append(quickPick.Name)
                        .Append(':')
                        .Append(quickPick.Unit)
                        .Append(':')
                        .Append(quickPick.Portion.ToString(CultureInfo.InvariantCulture))
                        .Append(':')
                        .Append(quickPick.Portions.ToString(CultureInfo.InvariantCulture))
                        .Append(':')
                        .Append(quickPick.Carbs.ToString(CultureInfo.InvariantCulture));
                }
            }

            return builder.ToString();
        }
    }

    /// <inheritdoc />
    public async Task<Food?> UpdateFoodAsync(
        string id,
        Food food,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Updating food record with ID: {Id}", id);

            var updatedFood = await _food.UpdateFoodAsync(id, food, cancellationToken);

            if (updatedFood != null)
            {
                await _sideEffects.OnUpdatedAsync(CollectionName, updatedFood, cancellationToken: cancellationToken);
                await _events.OnUpdatedAsync(updatedFood, cancellationToken);
                _logger.LogDebug("Successfully updated food record with ID: {Id}", id);
            }

            return updatedFood;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating food record with ID: {Id}", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteFoodAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Deleting food record with ID: {Id}", id);

            var deleted = await _food.DeleteFoodAsync(id, cancellationToken);

            if (deleted)
            {
                await _sideEffects.OnDeletedAsync<Food>(CollectionName, null, cancellationToken: cancellationToken);
                await _events.OnDeletedAsync(null, cancellationToken);
                _logger.LogDebug("Successfully deleted food record with ID: {Id}", id);
            }

            return deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting food record with ID: {Id}", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<long> DeleteMultipleFoodAsync(
        string? find = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Bulk deleting food records with filter: {Find}", find);

            var deletedCount = await _food.BulkDeleteFoodAsync(
                find ?? "{}",
                cancellationToken
            );

            if (deletedCount > 0)
            {
                await _sideEffects.OnBulkDeletedAsync(CollectionName, deletedCount, cancellationToken: cancellationToken);
                await _events.OnBulkDeletedAsync(deletedCount, cancellationToken);
                _logger.LogDebug("Successfully bulk deleted {Count} food records", deletedCount);
            }

            return deletedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bulk deleting food records with filter: {Find}", find);
            throw;
        }
    }
}
