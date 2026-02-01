using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Food controller that provides 1:1 compatibility with Nightscout food endpoints
/// Implements the /api/v1/food/* endpoints from the legacy JavaScript implementation
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class FoodController : ControllerBase
{
    private readonly IPostgreSqlService _postgreSqlService;
    private readonly ILogger<FoodController> _logger;

    public FoodController(IPostgreSqlService postgreSqlService, ILogger<FoodController> logger)
    {
        _postgreSqlService = postgreSqlService;
        _logger = logger;
    }

    /// <summary>
    /// Get all food records (both food and quickpick types)
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of food records ordered by name/position</returns>
    [HttpGet]
    [NightscoutEndpoint("/api/v1/food")]
    [ProducesResponseType(typeof(Food[]), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Food[]>> GetFood(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Food endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            var foods = await _postgreSqlService.GetFoodAsync(cancellationToken);
            var foodList = foods.ToList();

            // Note: Nightscout V1 GET /api/v1/food DOES NOT support find query parameters
            // It ignores any query parameters and returns all foods
            // We match this behavior for parity

            // Sort by created_at to ensure consistent ordering (Nightscout uses insertion order)
            foodList = foodList.OrderBy(f => f.CreatedAt).ToList();

            _logger.LogDebug("Successfully retrieved {Count} food records", foodList.Count);

            // Set response headers for caching
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Last-Modified"] = DateTimeOffset.UtcNow.ToString("R");

            return Ok(foodList.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving food records");
            return StatusCode(500, "Internal server error while retrieving food records");
        }
    }

    /// <summary>
    /// Alternative endpoint with .json extension for compatibility
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of food records</returns>
    [HttpGet("~/api/v1/food.json")]
    [NightscoutEndpoint("/api/v1/food.json")]
    [ProducesResponseType(typeof(Food[]), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Food[]>> GetFoodJson(
        CancellationToken cancellationToken = default
    )
    {
        return await GetFood(cancellationToken);
    }

    /// <summary>
    /// Get regular food records only (type="food")
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of regular food records</returns>
    [HttpGet("regular")]
    [NightscoutEndpoint("/api/v1/food/regular")]
    [ProducesResponseType(typeof(Food[]), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Food[]>> GetRegularFood(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Regular food endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            var foods = await _postgreSqlService.GetFoodByTypeAsync("food", cancellationToken);
            var foodArray = foods.ToArray();

            _logger.LogDebug(
                "Successfully retrieved {Count} regular food records",
                foodArray.Length
            );

            // Set response headers for caching
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Last-Modified"] = DateTimeOffset.UtcNow.ToString("R");

            return Ok(foodArray);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving regular food records");
            return StatusCode(500, "Internal server error while retrieving regular food records");
        }
    }

    /// <summary>
    /// Get quickpick food records only (type="quickpick" and hidden="false")
    /// Matches Nightscout's listquickpicks behavior which filters by hidden='false' and sorts by position
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of quickpick food records ordered by position</returns>
    [HttpGet("quickpicks")]
    [NightscoutEndpoint("/api/v1/food/quickpicks")]
    [ProducesResponseType(typeof(Food[]), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Food[]>> GetQuickPickFood(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Quickpick food endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            var foods = await _postgreSqlService.GetFoodByTypeAsync("quickpick", cancellationToken);

            // Nightscout's listquickpicks filters by hidden='false' (string in MongoDB)
            // Since we use boolean Hidden, filter where Hidden == false
            // Also sort by position ascending (Nightscout's sort({'position': 1}))
            var foodArray = foods
                .Where(f => !f.Hidden)
                .OrderBy(f => f.Position)
                .ToArray();

            _logger.LogDebug(
                "Successfully retrieved {Count} quickpick food records",
                foodArray.Length
            );

            // Set response headers for caching
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Last-Modified"] = DateTimeOffset.UtcNow.ToString("R");

            return Ok(foodArray);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving quickpick food records");
            return StatusCode(500, "Internal server error while retrieving quickpick food records");
        }
    }

    /// <summary>
    /// Get a specific food record by ID
    /// </summary>
    /// <param name="id">Food record ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The food record with the specified ID</returns>
    [HttpGet("{id}")]
    [NightscoutEndpoint("/api/v1/food/:id")]
    [ProducesResponseType(typeof(Food), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Food>> GetFoodById(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Food by ID endpoint requested for ID: {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                _logger.LogWarning("Invalid food ID: {Id}", id);
                return BadRequest("Food ID cannot be null or empty");
            }

            var food = await _postgreSqlService.GetFoodByIdAsync(id, cancellationToken);
            if (food == null)
            {
                _logger.LogDebug("Food record not found with ID: {Id}", id);
                return NotFound();
            }

            _logger.LogDebug("Successfully retrieved food record with ID: {Id}", id);

            // Set response headers for caching
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Last-Modified"] = DateTimeOffset.UtcNow.ToString("R");

            return Ok(food);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving food record with ID: {Id}", id);
            return StatusCode(500, $"Internal server error while retrieving food record {id}");
        }
    }

    /// <summary>
    /// Create new food records
    /// </summary>
    /// <param name="foods">Food records to create (can be single object or array)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created food records with assigned IDs</returns>
    [HttpPost]
    [NightscoutEndpoint("/api/v1/food")]
    [ProducesResponseType(typeof(Food[]), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Food[]>> CreateFood(
        [FromBody] JsonElement foods,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Create food endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            List<Food> foodsToCreate;

            // Handle both single food record and array of food records
            if (foods.ValueKind == JsonValueKind.Array)
            {
                foodsToCreate =
                    JsonSerializer.Deserialize<List<Food>>(foods.GetRawText()) ?? new List<Food>();
            }
            else if (foods.ValueKind == JsonValueKind.Object)
            {
                var singleFood = JsonSerializer.Deserialize<Food>(foods.GetRawText());
                foodsToCreate =
                    singleFood != null ? new List<Food> { singleFood } : new List<Food>();
            }
            else
            {
                _logger.LogWarning("Invalid JSON format for food records");
                return BadRequest("Invalid JSON format. Expected object or array of food records.");
            }

            if (foodsToCreate.Count == 0)
            {
                _logger.LogWarning("No food records provided for creation");
                return BadRequest("No food records provided");
            }

            // Validate and prepare food records
            foreach (var food in foodsToCreate)
            {
                // Set default type if not provided
                if (string.IsNullOrWhiteSpace(food.Type))
                {
                    food.Type = "food";
                }

                // Validate type
                if (food.Type != "food" && food.Type != "quickpick")
                {
                    return BadRequest(
                        $"Invalid food type: '{food.Type}'. Must be 'food' or 'quickpick'."
                    );
                }

                // Nightscout V1 API does NOT reject missing name - it creates the record anyway
                // This matches legacy Nightscout behavior

                // Set default unit if not provided
                if (string.IsNullOrWhiteSpace(food.Unit))
                {
                    food.Unit = "g";
                }

                // Set default GI if not provided or invalid
                if (food.Gi < 1 || food.Gi > 3)
                {
                    food.Gi = 2;
                }

                // Set created_at timestamp for parity with Nightscout
                if (string.IsNullOrWhiteSpace(food.CreatedAt))
                {
                    food.CreatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                }
            }

            var createdFoods = await _postgreSqlService.CreateFoodAsync(
                foodsToCreate,
                cancellationToken
            );
            var resultArray = createdFoods.ToArray();

            _logger.LogDebug("Successfully created {Count} food records", resultArray.Length);

            // Set response headers
            Response.Headers["Location"] = $"/api/v1/food";

            // Nightscout returns 200 OK for POST, not 201 Created
            return Ok(resultArray);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON in create food request");
            return BadRequest("Invalid JSON format");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating food records");
            return StatusCode(500, "Internal server error while creating food records");
        }
    }

    /// <summary>
    /// Update an existing food record by ID
    /// </summary>
    /// <param name="id">Food record ID to update</param>
    /// <param name="food">Updated food data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated food record</returns>
    [HttpPut("{id}")]
    [NightscoutEndpoint("/api/v1/food/:id")]
    [ProducesResponseType(typeof(Food), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Food>> UpdateFood(
        string id,
        [FromBody] Food food,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Update food endpoint requested for ID: {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                _logger.LogWarning("Invalid food ID: {Id}", id);
                return BadRequest("Food ID cannot be null or empty");
            }

            if (food == null)
            {
                _logger.LogWarning("Food data is null for update");
                return BadRequest("Food data cannot be null");
            }

            // Ensure the food has the correct ID
            food.Id = id;

            // Validate type
            if (food.Type != "food" && food.Type != "quickpick")
            {
                return BadRequest(
                    $"Invalid food type: '{food.Type}'. Must be 'food' or 'quickpick'."
                );
            }

            // Validate required fields
            if (string.IsNullOrWhiteSpace(food.Name))
            {
                return BadRequest("Food name is required");
            }

            // Set default unit if not provided
            if (string.IsNullOrWhiteSpace(food.Unit))
            {
                food.Unit = "g";
            }

            // Set default GI if not provided or invalid
            if (food.Gi < 1 || food.Gi > 3)
            {
                food.Gi = 2;
            }

            var updatedFood = await _postgreSqlService.UpdateFoodAsync(id, food, cancellationToken);
            if (updatedFood == null)
            {
                _logger.LogDebug("Food record not found for update with ID: {Id}", id);
                return NotFound();
            }

            _logger.LogDebug("Successfully updated food record with ID: {Id}", id);

            return Ok(updatedFood);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating food record with ID: {Id}", id);
            return StatusCode(500, $"Internal server error while updating food record {id}");
        }
    }

    /// <summary>
    /// Alternative update endpoint compatible with legacy PUT without ID in URL
    /// </summary>
    /// <param name="food">Food data with ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated food record</returns>
    [HttpPut]
    [NightscoutEndpoint("/api/v1/food")]
    [ProducesResponseType(typeof(Food), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<Food>> UpdateFoodLegacy(
        [FromBody] Food food,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Legacy update food endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        if (food?.Id == null)
        {
            _logger.LogWarning("Food ID is null in legacy update request");
            return BadRequest("Food ID is required for update");
        }

        return await UpdateFood(food.Id, food, cancellationToken);
    }

    /// <summary>
    /// Delete a food record by ID
    /// </summary>
    /// <param name="id">Food record ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>No content if successful</returns>
    [HttpDelete("{id}")]
    [NightscoutEndpoint("/api/v1/food/:id")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> DeleteFood(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Delete food endpoint requested for ID: {Id} from {RemoteIpAddress}",
            id,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                _logger.LogWarning("Invalid food ID: {Id}", id);
                return BadRequest("Food ID cannot be null or empty");
            }

            var deleted = await _postgreSqlService.DeleteFoodAsync(id, cancellationToken);
            if (!deleted)
            {
                _logger.LogDebug("Food record not found for deletion with ID: {Id}", id);
                return NotFound();
            }

            _logger.LogDebug("Successfully deleted food record with ID: {Id}", id);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting food record with ID: {Id}", id);
            return StatusCode(500, $"Internal server error while deleting food record {id}");
        }
    }

    /// <summary>
    /// Bulk delete food records by filter query
    /// Compatible with Nightscout's DELETE /api/v1/food?find[field]=value
    /// Note: Nightscout V1 API doesn't officially support bulk delete, but returns 200 {} for such requests
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Empty object for parity with Nightscout</returns>
    [HttpDelete]
    [NightscoutEndpoint("/api/v1/food")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> DeleteFoodByFilter(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Bulk delete food endpoint requested from {RemoteIpAddress}",
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Parse find query from query string
            var findQuery = ParseFindQuery();

            if (string.IsNullOrEmpty(findQuery))
            {
                // Nightscout V1 returns 200 {} when no filter is provided
                // This matches the legacy behavior where DELETE /api/v1/food doesn't exist
                // but Nightscout's routing returns an empty success response
                _logger.LogDebug("No filter query provided for bulk delete, returning empty response");
                return Ok(new { });
            }

            var deletedCount = await _postgreSqlService.BulkDeleteFoodAsync(findQuery, cancellationToken);

            _logger.LogDebug("Deleted {Count} food records matching filter: {Query}", deletedCount, findQuery);

            // Nightscout returns 200 {} for delete operations
            return Ok(new { });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bulk deleting food records");
            return StatusCode(500, "Internal server error while deleting food records");
        }
    }

    /// <summary>
    /// Parse find query from query string parameters
    /// Handles find[field]=value format
    /// </summary>
    /// <returns>JSON query string or null if no find parameters</returns>
    private string? ParseFindQuery()
    {
        var query = HttpContext.Request.Query;
        var findParams = new Dictionary<string, object>();

        foreach (var param in query)
        {
            if (param.Key.StartsWith("find[") && param.Key.EndsWith("]"))
            {
                var field = param.Key[5..^1]; // Extract field name from find[field]
                var value = param.Value.FirstOrDefault();
                if (value != null)
                {
                    findParams[field] = value;
                }
            }
        }

        if (findParams.Count == 0)
            return null;

        return System.Text.Json.JsonSerializer.Serialize(findParams);
    }
}
