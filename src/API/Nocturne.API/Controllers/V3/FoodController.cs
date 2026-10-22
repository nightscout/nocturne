using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Configuration;
using Nocturne.API.Authorization;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Contracts.Repositories;

namespace Nocturne.API.Controllers.V3;

/// <summary>
/// V3 Food controller that provides full V3 API compatibility with Nightscout food endpoints.
/// Implements the /api/v3/food endpoints with pagination, field selection, sorting, and advanced filtering.
/// </summary>
/// <seealso cref="IFoodRepository"/>
/// <seealso cref="Food"/>
/// <seealso cref="BaseV3Controller{T}"/>
[ApiController]
[Tags("V3")]
[Route("api/v3/[controller]")]
[Authorize(Policy = PolicyNames.HasPermissions)]
public class FoodController : BaseV3Controller<Food>
{
    private readonly IFoodRepository _foods;

    public FoodController(
        IFoodRepository foods,
        IDocumentProcessingService documentProcessingService,
        ILogger<FoodController> logger
    )
        : base(documentProcessingService, logger)
    {
        _foods = foods;
    }

    /// <summary>
    /// Get food records with V3 API features including pagination, field selection, and advanced filtering
    /// </summary>
    /// <returns>V3 food collection response</returns>
    [HttpGet]
    [NightscoutEndpoint("/api/v3/food")]
    [ProducesResponseType(typeof(V3CollectionResponse<object>), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(304)]
    [ProducesResponseType(500)]
    [RequireScope(Scope.FoodRead)]
    [ErrorEnvelope]
    public async Task<ActionResult> GetFood(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "V3 food endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            var parameters = ParseV3QueryParameters();

            // Extract type filter from V3 filter criteria (type$eq=food or type$eq=quickpick)
            var typeFilter = parameters.FilterCriteria.FirstOrDefault(f =>
                f.Field.Equals("type", StringComparison.OrdinalIgnoreCase) && f.Operator == "eq"
            );
            var type = typeFilter?.Value?.ToString();

            // Get all food records - we'll apply V3 filtering in memory
            var foodRecords = await _foods.GetFoodWithAdvancedFilterAsync(
                count: int.MaxValue, // Get all, filter later
                skip: 0,
                findQuery: null,
                type: type,
                cancellationToken: cancellationToken
            );

            var foodList = foodRecords.ToList();

            // Apply V3 filter criteria
            foodList = ApplyV3FilterCriteria(foodList, parameters.FilterCriteria);
            foodList = OrderFood(foodList, parameters.Sort);

            // Apply pagination
            foodList = foodList.Skip(parameters.Offset).Take(parameters.Limit).ToList();

            // Check for conditional requests (304 Not Modified)
            var lastModified = GetLastModified(foodList.Cast<object>());

            if (lastModified.HasValue && ShouldReturn304(lastModified.Value, parameters))
            {
                return StatusCode(304);
            }

            _logger.LogDebug(
                "Successfully returned {Count} food records with V3 format",
                foodList.Count
            );

            // CreateV3CollectionResponse returns the properly formatted IActionResult
            return (ActionResult)CreateV3CollectionResponse(
                foodList.Select(f => ToV3Document(f, f.Id)).ToList(),
                parameters,
                lastModified
            );
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 food request parameters");
            return CreateV3ErrorResponse(400, "Invalid request parameters", ex.Message);
        }
    }

    /// <summary>
    /// Get food records modified since a given timestamp (for AAPS incremental sync).
    /// </summary>
    /// <param name="lastModified">Unix timestamp in milliseconds. Only foods modified after this time are returned.</param>
    /// <param name="limit">Maximum number of foods to return (1-1000, default 1000).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>V3 collection of <see cref="Food"/> records modified since the given timestamp.</returns>
    /// <remarks>
    /// AAPS requires a parseable cursor ETag on this response even when it is empty (its food
    /// loader throws on a missing ETag), so the request cursor is echoed back when no newer
    /// records exist.
    /// </remarks>
    /// <response code="200">Foods modified since the given timestamp.</response>
    /// <response code="500">Internal server error.</response>
    [HttpGet("history/{lastModified:long}")]
    [NightscoutEndpoint("/api/v3/food/history/{lastModified}")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(500)]
    [RequireScope(Scope.FoodRead)]
    [ErrorEnvelope]
    public async Task<ActionResult> GetFoodHistory(
        long lastModified,
        [FromQuery] int limit = 1000,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 food history requested since {LastModified} with limit {Limit}",
            lastModified,
            limit
        );

        limit = Math.Min(Math.Max(limit, 1), 1000);

        var page = await _foods.GetFoodModifiedSinceAsync(lastModified, limit, cancellationToken);

        SetHistoryCursorHeaders(page.CursorMills ?? lastModified);

        return CreateV3SuccessResponse(page.Records.Select(f => ToV3Document(f, f.Id)).ToList());
    }

    /// <summary>
    /// Apply V3 filter criteria to a list of food records
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Func<Food, object?>> SortKeys =
        new Dictionary<string, Func<Food, object?>>
        {
            ["name"] = f => f.Name,
            ["category"] = f => f.Category,
            ["subcategory"] = f => f.Subcategory,
            ["type"] = f => f.Type,
            ["unit"] = f => f.Unit,
            ["portion"] = f => f.Portion,
            ["carbs"] = f => f.Carbs,
            ["fat"] = f => f.Fat,
            ["protein"] = f => f.Protein,
            ["energy"] = f => f.Energy,
            ["gi"] = f => f.Gi,
            ["position"] = f => f.Position,
            ["created_at"] = f => f.CreatedAt,
            ["srvModified"] = f => f.SrvModified,
            ["srvCreated"] = f => f.SrvCreated,
        };

    /// <summary>
    /// Orders the filtered foods by the sort field when it is one of <see cref="SortKeys"/>, and by
    /// name otherwise, breaking ties by name then identifier in the same direction.
    /// </summary>
    private static List<Food> OrderFood(List<Food> foods, V3Sort sort)
    {
        var key = SortKeys.GetValueOrDefault(sort.Field ?? string.Empty, SortKeys["name"]);
        var comparer = Comparer<object?>.Create(CompareSortKeys);

        return (sort.Descending
                ? foods.OrderByDescending(key, comparer)
                    .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                    .ThenByDescending(f => f.Id, StringComparer.Ordinal)
                : foods.OrderBy(key, comparer)
                    .ThenBy(f => f.Name, StringComparer.Ordinal)
                    .ThenBy(f => f.Id, StringComparer.Ordinal))
            .ToList();
    }

    private static int CompareSortKeys(object? left, object? right) =>
        (left, right) switch
        {
            (null, null) => 0,
            (null, _) => -1,
            (_, null) => 1,
            (string a, string b) => string.CompareOrdinal(a, b),
            _ => ((IComparable)left).CompareTo(right),
        };

    private List<Food> ApplyV3FilterCriteria(List<Food> foods, List<V3FilterCriteria> criteria)
    {
        if (criteria == null || criteria.Count == 0)
            return foods;

        foreach (var filter in criteria)
        {
            // Skip type filter as it's already applied at the database level
            if (filter.Field.Equals("type", StringComparison.OrdinalIgnoreCase))
                continue;

            var regex = filter is { Operator: "re", Value: string pattern } ? BoundedRegex.Create(pattern) : null;
            foods = foods.Where(f => MatchesFilter(f, filter, regex)).ToList();
            if (regex?.Abandoned == true)
                _logger.LogWarning("A food $re filter ran out of its time budget; matches may be incomplete");
        }

        return foods;
    }

    /// <summary>
    /// Check if a food record matches a filter criterion
    /// </summary>
    private bool MatchesFilter(Food food, V3FilterCriteria filter, BoundedRegex? regex)
    {
        var fieldValue = GetFieldValue(food, filter.Field);
        if (fieldValue == null && filter.Value == null)
            return true;
        if (fieldValue == null || filter.Value == null)
            return false;

        return filter.Operator switch
        {
            "eq" => CompareValues(fieldValue, filter.Value) == 0,
            "ne" => CompareValues(fieldValue, filter.Value) != 0,
            "gt" => CompareValues(fieldValue, filter.Value) > 0,
            "gte" => CompareValues(fieldValue, filter.Value) >= 0,
            "lt" => CompareValues(fieldValue, filter.Value) < 0,
            "lte" => CompareValues(fieldValue, filter.Value) <= 0,
            "in" => filter.Value is string s && s.Split(',').Contains(fieldValue.ToString()),
            "nin" => filter.Value is string s2 && !s2.Split(',').Contains(fieldValue.ToString()),
            "re" => regex?.IsMatch(fieldValue.ToString() ?? "") == true,
            _ => true,
        };
    }

    /// <summary>
    /// Get field value from a food object using reflection
    /// </summary>
    private object? GetFieldValue(Food food, string fieldName)
    {
        if (fieldName.Equals("_id", StringComparison.OrdinalIgnoreCase)
            || fieldName.Equals("identifier", StringComparison.OrdinalIgnoreCase))
            return MongoObjectId.Coerce(food.Id);

        // Map lowercase field names to property names
        var propertyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = "Type",
            ["category"] = "Category",
            ["subcategory"] = "Subcategory",
            ["name"] = "Name",
            ["portion"] = "Portion",
            ["carbs"] = "Carbs",
            ["fat"] = "Fat",
            ["protein"] = "Protein",
            ["energy"] = "Energy",
            ["gi"] = "Gi",
            ["unit"] = "Unit",
            ["hidden"] = "Hidden",
            ["hideafteruse"] = "HideAfterUse",
            ["position"] = "Position",
        };

        if (!propertyMap.TryGetValue(fieldName, out var propName))
            propName = fieldName;

        var prop = typeof(Food).GetProperty(propName);
        return prop?.GetValue(food);
    }

    /// <summary>
    /// Compare two values for filtering
    /// </summary>
    private int CompareValues(object a, object b)
    {
        // Try numeric comparison
        if (
            double.TryParse(a.ToString(), out var numA)
            && double.TryParse(b.ToString(), out var numB)
        )
        {
            return numA.CompareTo(numB);
        }

        // String comparison (case-insensitive)
        return string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get a specific food record by ID with V3 format
    /// </summary>
    /// <param name="id">Food ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Single food record in V3 format</returns>
    [HttpGet("{id}")]
    [NightscoutEndpoint("/api/v3/food/{id}")]
    [ProducesResponseType(typeof(Food), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(500)]
    [RequireScope(Scope.FoodRead)]
    [ErrorEnvelope]
    public async Task<ActionResult> GetFoodById(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 food by ID endpoint requested for ID {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        var food = await _foods.GetFoodByIdAsync(id, cancellationToken);

        if (food == null)
        {
            return CreateV3ErrorResponse(
                404,
                "Food not found",
                $"Food with ID '{id}' was not found"
            );
        }

        var parameters = ParseV3QueryParameters(readSort: false); // Apply field selection if specified
        var result = ApplyFieldSelection(new[] { ToV3Document(food, food.Id) }, parameters.Fields)
            .FirstOrDefault();

        _logger.LogDebug("Successfully returned food with ID {Id}", id);

        return Ok(result);
    }

    /// <summary>
    /// Create new food records with V3 format and deduplication support
    /// Nightscout V3 API requires date field validation before processing
    /// </summary>
    /// <param name="foodData">Food data to create (single object or array)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The <see cref="V3CreateResponse"/> for one document; the created records for an array.</returns>
    [HttpPost]
    [Authorize]
    [RequireScope(Scope.FoodReadWrite)]
    [NightscoutEndpoint("/api/v3/food")]
    [ProducesResponseType(typeof(V3CreateResponse), 201)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public async Task<ActionResult> CreateFood(
        [FromBody] JsonElement foodData,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 food create endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );
        try
        {
            // Check for empty request body first (Nightscout returns "Bad or missing request body")
            if (IsEmptyRequestBody(foodData))
            {
                return CreateV3ErrorResponse(400, "Bad or missing request body");
            }

            // Nightscout V3 API validates common fields (including date) before processing
            // For arrays, validate each element; for single objects, validate directly
            if (foodData.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in foodData.EnumerateArray())
                {
                    var validationError = ValidateV3CommonFields(element);
                    if (validationError != null)
                    {
                        return validationError;
                    }
                }
            }
            else if (foodData.ValueKind == JsonValueKind.Object)
            {
                var validationError = ValidateV3CommonFields(foodData);
                if (validationError != null)
                {
                    return validationError;
                }
            }

            var foodRecords = ParseCreateRequestFromJsonElement(foodData);

            if (!foodRecords.Any())
            {
                return CreateV3ErrorResponse(
                    400,
                    "Invalid request body",
                    "Request body must contain valid food data"
                );
            }

            // Process each food record (date parsing, validation, etc.)
            foreach (var food in foodRecords)
            {
                ProcessFoodForCreation(food);
            }

            // Create food records with deduplication support
            var createdRecords = await _foods.CreateFoodAsync(
                foodRecords,
                cancellationToken
            );

            _logger.LogDebug("Successfully created {Count} food records", createdRecords.Count());

            if (foodData.ValueKind == JsonValueKind.Array)
            {
                return StatusCode(201, createdRecords.ToArray());
            }

            var created = createdRecords.FirstOrDefault();
            if (created == null)
            {
                return CreateV3ErrorResponse(500, "Failed to create food", "Food creation failed");
            }

            return V3Created(nameof(GetFoodById), created.Id);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 food create request");
            return CreateV3ErrorResponse(400, "Invalid request data", ex.Message);
        }
    }

    /// <summary>
    /// Update a food record by ID with V3 format
    /// Nightscout V3 API requires date field validation before checking document existence
    /// </summary>
    /// <param name="id">Food ID to update</param>
    /// <param name="foodData">Updated food data as JSON</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated food record</returns>
    [HttpPut("{id}")]
    [Authorize]
    [RequireScope(Scope.FoodReadWrite)]
    [NightscoutEndpoint("/api/v3/food/{id}")]
    [ProducesResponseType(typeof(Food), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public async Task<ActionResult> UpdateFood(
        string id,
        [FromBody] JsonElement foodData,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 food update endpoint requested for ID {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Nightscout V3 API validates common fields before checking if document exists
            // This includes the 'date' field which must be a number > MIN_TIMESTAMP (946684800000)
            var validationError = ValidateV3CommonFields(foodData);
            if (validationError != null)
            {
                return validationError;
            }

            var food = NightscoutJsonBody.ReadObject<Food>(foodData);

            if (food == null)
            {
                return CreateV3ErrorResponse(
                    400,
                    "Invalid request body",
                    "Request body must contain valid food data"
                );
            }

            ProcessFoodForCreation(food);

            var updatedFood = await _foods.UpdateFoodAsync(id, food, cancellationToken);

            if (updatedFood == null)
            {
                return CreateV3ErrorResponse(
                    404,
                    "Food not found",
                    $"Food with ID '{id}' was not found"
                );
            }

            _logger.LogDebug("Successfully updated food with ID {Id}", id);

            return Ok(updatedFood);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON in V3 food update request for ID {Id}", id);
            return CreateV3ErrorResponse(400, "Invalid request data", ex.Message);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 food update request for ID {Id}", id);
            return CreateV3ErrorResponse(400, "Invalid request data", ex.Message);
        }
    }

    /// <summary>
    /// Check if the request body is empty (Nightscout returns specific error for this)
    /// </summary>
    /// <param name="jsonData">The JSON data to check</param>
    /// <returns>True if the request body is empty</returns>
    private bool IsEmptyRequestBody(JsonElement jsonData)
    {
        if (jsonData.ValueKind == JsonValueKind.Undefined)
            return true;

        if (jsonData.ValueKind == JsonValueKind.Null)
            return true;

        if (jsonData.ValueKind == JsonValueKind.Object)
        {
            // Empty object {} is considered empty
            using var enumerator = jsonData.EnumerateObject();
            return !enumerator.MoveNext();
        }

        if (jsonData.ValueKind == JsonValueKind.Array)
        {
            // Empty array [] is considered empty
            return jsonData.GetArrayLength() == 0;
        }

        return false;
    }

    /// <summary>
    /// Delete a food record by ID
    /// </summary>
    /// <param name="id">Food ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns><c>{"status": 200}</c> on success</returns>
    [HttpDelete("{id}")]
    [Authorize]
    [RequireScope(Scope.FoodReadWrite)]
    [NightscoutEndpoint("/api/v3/food/{id}")]
    [ProducesResponseType(200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public async Task<ActionResult> DeleteFood(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "V3 food delete endpoint requested for ID {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        var deleted = await _foods.DeleteFoodAsync(id, cancellationToken);

        if (!deleted)
        {
            return CreateV3ErrorResponse(
                404,
                "Food not found",
                $"Food with ID '{id}' was not found"
            );
        }

        _logger.LogDebug("Successfully deleted food with ID {Id}", id);

        return CreateV3DeleteResponse();
    }

    /// <summary>
    /// Process food record for creation/update (date parsing, validation, etc.)
    /// Follows the legacy API v3 behavior exactly
    /// </summary>
    /// <param name="food">Food record to process</param>
    private void ProcessFoodForCreation(Food food)
    {
        // Generate identifier if not present (legacy behavior)
        if (string.IsNullOrEmpty(food.Id))
        {
            food.Id = GenerateIdentifier(food);
        }

        // Validate food type (food or quickpick)
        if (string.IsNullOrEmpty(food.Type))
        {
            food.Type = "food"; // Default to "food" type
        }

        // Ensure food name is present
        if (string.IsNullOrEmpty(food.Name))
        {
            throw new ArgumentException("Food name is required");
        }
    }

    /// <summary>
    /// Generate identifier for food record following legacy API v3 logic
    /// Uses food name for food deduplication fallback
    /// </summary>
    /// <param name="food">Food record</param>
    /// <returns>Generated identifier</returns>
    private string GenerateIdentifier(Food food)
    {
        // Legacy API v3 uses food name for food deduplication
        var identifierParts = new List<string>();

        // Add food name for identification
        if (!string.IsNullOrEmpty(food.Name))
        {
            identifierParts.Add(food.Name.Replace(" ", "-"));
        }

        // Add timestamp for uniqueness
        identifierParts.Add(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));

        // If we have identifying parts, create a hash-based identifier
        if (identifierParts.Any())
        {
            var combined = string.Join("-", identifierParts);
            return $"food-{combined.GetHashCode():X}";
        }

        // Fallback to GUID for unique identification
        return Guid.CreateVersion7().ToString();
    }

    /// <summary>
    /// Parse create request from JsonElement for Food objects
    /// </summary>
    /// <param name="jsonElement">JsonElement containing food data (single object or array)</param>
    /// <returns>Collection of Food objects</returns>
    private IEnumerable<Food> ParseCreateRequestFromJsonElement(JsonElement jsonElement)
    {
        try
        {
            return NightscoutJsonBody.ReadObjects<Food>(jsonElement) ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse food data from JsonElement");
            throw new ArgumentException("Invalid food data format", ex);
        }
    }

}
