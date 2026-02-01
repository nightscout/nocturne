using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Controllers.V3;

/// <summary>
/// V3 Food controller that provides full V3 API compatibility with Nightscout food endpoints
/// Implements the /api/v3/food endpoints with pagination, field selection, sorting, and advanced filtering
/// </summary>
[ApiController]
[Route("api/v3/[controller]")]
public class FoodController : BaseV3Controller<Food>
{
    public FoodController(
        IPostgreSqlService postgreSqlService,
        IDataFormatService dataFormatService,
        IDocumentProcessingService documentProcessingService,
        ILogger<FoodController> logger
    )
        : base(postgreSqlService, dataFormatService, documentProcessingService, logger) { }

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

            // Determine sort direction from sort$desc query parameter
            // Nightscout V3: sort$desc=field means descending (newest first)
            // reverseResults=false means descending, reverseResults=true means ascending
            var hasSortDesc = HttpContext?.Request.Query.ContainsKey("sort$desc") ?? false;
            var reverseResults = !hasSortDesc && ExtractSortDirection(parameters.Sort);

            // Get all food records - we'll apply V3 filtering in memory
            var foodRecords = await _postgreSqlService.GetFoodWithAdvancedFilterAsync(
                count: int.MaxValue, // Get all, filter later
                skip: 0,
                findQuery: null,
                type: type,
                reverseResults: reverseResults,
                cancellationToken: cancellationToken
            );

            var foodList = foodRecords.ToList();

            // Apply V3 filter criteria
            foodList = ApplyV3FilterCriteria(foodList, parameters.FilterCriteria);

            // Get total count before pagination
            var totalCount = foodList.Count;

            // Apply pagination
            foodList = foodList.Skip(parameters.Offset).Take(parameters.Limit).ToList();

            // Check for conditional requests (304 Not Modified)
            var lastModified = GetLastModified(foodList.Cast<object>());
            var etag = GenerateETag(foodList);

            if (lastModified.HasValue && ShouldReturn304(etag, lastModified.Value, parameters))
            {
                return StatusCode(304);
            }

            _logger.LogDebug(
                "Successfully returned {Count} food records with V3 format",
                foodList.Count
            );

            // CreateV3CollectionResponse returns the properly formatted IActionResult
            return (ActionResult)CreateV3CollectionResponse(foodList, parameters, totalCount);
        }
        catch (V3ParameterOutOfToleranceException ex)
        {
            _logger.LogWarning(ex, "V3 parameter out of tolerance: {Parameter}", ex.ParameterName);
            return CreateV3ErrorResponse(400, $"Parameter {ex.ParameterName} out of tolerance");
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 food request parameters");
            return CreateV3ErrorResponse(400, "Invalid request parameters", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving V3 food");
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Apply V3 filter criteria to a list of food records
    /// </summary>
    private List<Food> ApplyV3FilterCriteria(List<Food> foods, List<V3FilterCriteria> criteria)
    {
        if (criteria == null || criteria.Count == 0)
            return foods;

        foreach (var filter in criteria)
        {
            // Skip type filter as it's already applied at the database level
            if (filter.Field.Equals("type", StringComparison.OrdinalIgnoreCase))
                continue;

            foods = foods.Where(f => MatchesFilter(f, filter)).ToList();
        }

        return foods;
    }

    /// <summary>
    /// Check if a food record matches a filter criterion
    /// </summary>
    private bool MatchesFilter(Food food, V3FilterCriteria filter)
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
            "re" => filter.Value is string pattern
                && System.Text.RegularExpressions.Regex.IsMatch(
                    fieldValue.ToString() ?? "",
                    pattern
                ),
            _ => true,
        };
    }

    /// <summary>
    /// Get field value from a food object using reflection
    /// </summary>
    private object? GetFieldValue(Food food, string fieldName)
    {
        // Map lowercase field names to property names
        var propertyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["_id"] = "Id",
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

        try
        {
            var food = await _postgreSqlService.GetFoodByIdAsync(id, cancellationToken);

            if (food == null)
            {
                return CreateV3ErrorResponse(
                    404,
                    "Food not found",
                    $"Food with ID '{id}' was not found"
                );
            }

            var parameters = ParseV3QueryParameters(); // Apply field selection if specified
            var result = ApplyFieldSelection(new[] { food }, parameters.Fields).FirstOrDefault();

            _logger.LogDebug("Successfully returned food with ID {Id}", id);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving food with ID {Id}", id);
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
    }

    /// <summary>
    /// Create new food records with V3 format and deduplication support
    /// Nightscout V3 API requires date field validation before processing
    /// </summary>
    /// <param name="foodData">Food data to create (single object or array)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created food records</returns>
    [HttpPost]
    [NightscoutEndpoint("/api/v3/food")]
    [ProducesResponseType(typeof(Food[]), 201)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(500)]
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
            var createdRecords = await _postgreSqlService.CreateFoodAsync(
                foodRecords,
                cancellationToken
            );

            _logger.LogDebug("Successfully created {Count} food records", createdRecords.Count());

            return StatusCode(201, createdRecords.ToArray());
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 food create request");
            return CreateV3ErrorResponse(400, "Invalid request data", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating V3 food");
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
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
    [NightscoutEndpoint("/api/v3/food/{id}")]
    [ProducesResponseType(typeof(Food), 200)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(typeof(V3ErrorResponse), 400)]
    [ProducesResponseType(500)]
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

            var food = JsonSerializer.Deserialize<Food>(
                foodData.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            if (food == null)
            {
                return CreateV3ErrorResponse(
                    400,
                    "Invalid request body",
                    "Request body must contain valid food data"
                );
            }

            ProcessFoodForCreation(food);

            var updatedFood = await _postgreSqlService.UpdateFoodAsync(id, food, cancellationToken);

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
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid V3 food update request for ID {Id}", id);
            return CreateV3ErrorResponse(400, "Invalid request data", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating food with ID {Id}", id);
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
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
    /// Validates common V3 fields as per Nightscout API behavior.
    /// Nightscout validates these fields before checking if the document exists.
    /// </summary>
    /// <param name="jsonData">The JSON data to validate</param>
    /// <returns>ActionResult with error if validation fails, null if validation passes</returns>
    private ActionResult? ValidateV3CommonFields(JsonElement jsonData)
    {
        const long MinTimestamp = 946684800000; // Year 2000 in milliseconds

        // Check for 'date' field - Nightscout requires this for all V3 operations
        if (!jsonData.TryGetProperty("date", out var dateProperty))
        {
            return CreateV3ErrorResponse(400, "Bad or missing date field");
        }

        // Date must be a number greater than MIN_TIMESTAMP
        if (dateProperty.ValueKind != JsonValueKind.Number)
        {
            return CreateV3ErrorResponse(400, "Bad or missing date field");
        }

        if (!dateProperty.TryGetInt64(out var dateValue) || dateValue <= MinTimestamp)
        {
            return CreateV3ErrorResponse(400, "Bad or missing date field");
        }

        return null; // Validation passed
    }

    /// <summary>
    /// Delete a food record by ID
    /// </summary>
    /// <param name="id">Food ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>No content on success</returns>
    [HttpDelete("{id}")]
    [NightscoutEndpoint("/api/v3/food/{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(V3ErrorResponse), 404)]
    [ProducesResponseType(500)]
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

        try
        {
            var deleted = await _postgreSqlService.DeleteFoodAsync(id, cancellationToken);

            if (!deleted)
            {
                return CreateV3ErrorResponse(
                    404,
                    "Food not found",
                    $"Food with ID '{id}' was not found"
                );
            }

            _logger.LogDebug("Successfully deleted food with ID {Id}", id);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting food with ID {Id}", id);
            return CreateV3ErrorResponse(500, "Internal server error", ex.Message);
        }
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
        var foodRecords = new List<Food>();

        try
        {
            if (jsonElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in jsonElement.EnumerateArray())
                {
                    var food = JsonSerializer.Deserialize<Food>(
                        element.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );
                    if (food != null)
                    {
                        foodRecords.Add(food);
                    }
                }
            }
            else if (jsonElement.ValueKind == JsonValueKind.Object)
            {
                var food = JsonSerializer.Deserialize<Food>(
                    jsonElement.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );
                if (food != null)
                {
                    foodRecords.Add(food);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse food data from JsonElement");
            throw new ArgumentException("Invalid food data format", ex);
        }

        return foodRecords;
    }

    /// <summary>
    /// Get total count for pagination support
    /// </summary>
    private async Task<long> GetTotalCountAsync(
        string? type,
        string? findQuery,
        CancellationToken cancellationToken,
        string collection = "food"
    )
    {
        try
        {
            return await _postgreSqlService.CountFoodAsync(findQuery, type, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not get total count for {Collection}, using approximation",
                collection
            );
            return 0; // Return 0 for count errors to maintain API functionality
        }
    }
}
