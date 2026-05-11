using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Controllers.V4.Treatments;

/// <summary>
/// Controller for food favorites, recent foods, and food lifecycle management.
/// </summary>
/// <remarks>
/// Provides a V4-native (non-Nightscout-legacy) food catalog in addition to per-user
/// favorites and recently-used lists.
///
/// <b>Food catalog</b> (<c>/api/v4/foods</c>) — CRUD for <see cref="Food"/> records via <see cref="IFoodService"/>.
/// The catalog is shared and not user-scoped.
///
/// <b>Favorites / recents</b> — user-scoped via <see cref="IUserFoodFavoriteService"/>, keyed by
/// the authenticated subject ID (falls back to a default system ID when the claim is absent).
///
/// <b>Attribution count</b> (<c>/{foodId}/attribution-count</c>) — reports how many carb intake
/// records reference a food, surfaced by <see cref="ITreatmentFoodService"/>. This count is used
/// to warn users before deleting a food that has meal attributions.
///
/// <b>Delete</b> (<c>DELETE /{foodId}</c>) — accepts an <c>attributionMode</c> query parameter:
/// <c>clear</c> (default) sets food references on attributions to <c>Other</c>;
/// <c>remove</c> deletes the attributions entirely.
/// </remarks>
/// <seealso cref="IFoodService"/>
/// <seealso cref="IUserFoodFavoriteService"/>
/// <seealso cref="ITreatmentFoodService"/>
/// <seealso cref="Food"/>
[ApiController]
[Tags("Treatments")]
[Route("api/v4/foods")]
[ClientPropertyName("foodsV4")]
public class FoodsController : ControllerBase
{
    private const string DefaultUserId = "00000000-0000-0000-0000-000000000001";

    private readonly NocturneDbContext _context;
    private readonly IUserFoodFavoriteService _favoriteService;
    private readonly ITreatmentFoodService _treatmentFoodService;
    private readonly IFoodService _foodService;

    public FoodsController(
        NocturneDbContext context,
        IUserFoodFavoriteService favoriteService,
        ITreatmentFoodService treatmentFoodService,
        IFoodService foodService)
    {
        _context = context;
        _favoriteService = favoriteService;
        _treatmentFoodService = treatmentFoodService;
        _foodService = foodService;
    }

    #region Food Catalog (V4, non-legacy)

    /// <summary>
    /// List foods with optional filtering and pagination.
    /// This is a V4 endpoint (not Nightscout-legacy) used by the meal attribution UI.
    /// </summary>
    [HttpGet]
    [RemoteQuery]
    [Authorize]
    [ProducesResponseType(typeof(Food[]), StatusCodes.Status200OK)]
    public async Task<ActionResult<Food[]>> GetFoods(
        [FromQuery] string? find = null,
        [FromQuery] int? count = null,
        [FromQuery] int? skip = null,
        CancellationToken ct = default)
    {
        var foods = await _foodService.GetFoodAsync(find, count, skip, ct);
        return Ok(foods.ToArray());
    }

    /// <summary>
    /// Get a single food by ID.
    /// </summary>
    [HttpGet("{foodId}")]
    [RemoteQuery]
    [Authorize]
    [ProducesResponseType(typeof(Food), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Food>> GetFood(string foodId, CancellationToken ct = default)
    {
        var food = await _foodService.GetFoodByIdAsync(foodId, ct);
        return food is null ? NotFound() : Ok(food);
    }

    /// <summary>
    /// Create a new food record.
    /// </summary>
    [HttpPost]
    [RemoteCommand(Invalidates = ["GetFoods", "GetFavorites", "GetRecentFoods"])]
    [Authorize]
    [ProducesResponseType(typeof(Food), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Food>> CreateFood([FromBody] Food food, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(food.Name))
            return Problem(detail: "Name is required", statusCode: 400, title: "Bad Request");

        var created = (await _foodService.CreateFoodAsync([food], ct)).ToArray();
        var first = created.FirstOrDefault();
        if (first is null)
            return Problem(detail: "Failed to create food", statusCode: 500, title: "Internal Server Error");

        return StatusCode(StatusCodes.Status201Created, first);
    }

    /// <summary>
    /// Update an existing food record by ID.
    /// </summary>
    [HttpPut("{foodId}")]
    [RemoteCommand(Invalidates = ["GetFoods", "GetFood", "GetFavorites", "GetRecentFoods"])]
    [Authorize]
    [ProducesResponseType(typeof(Food), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Food>> UpdateFood(string foodId, [FromBody] Food food, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(food.Name))
            return Problem(detail: "Name is required", statusCode: 400, title: "Bad Request");

        var updated = await _foodService.UpdateFoodAsync(foodId, food, ct);
        return updated is null ? NotFound() : Ok(updated);
    }

    #endregion

    /// <summary>
    /// Get current user's favorite foods.
    /// </summary>
    [HttpGet("favorites")]
    [RemoteQuery]
    [Authorize]
    public async Task<ActionResult<Food[]>> GetFavorites()
    {
        var userId = ResolveUserId();

        var favorites = await _favoriteService.GetFavoritesAsync(
            userId,
            HttpContext.RequestAborted
        );

        return Ok(favorites.ToArray());
    }

    /// <summary>
    /// Add a food to favorites.
    /// </summary>
    [HttpPost("{foodId}/favorite")]
    [RemoteCommand(Invalidates = ["GetFavorites"])]
    [Authorize]
    public async Task<ActionResult> AddFavorite(string foodId)
    {
        var userId = ResolveUserId();

        var food = await ResolveFoodEntityAsync(foodId, HttpContext.RequestAborted);
        if (food == null)
        {
            return NotFound();
        }

        await _favoriteService.AddFavoriteAsync(
            userId,
            food.Id,
            HttpContext.RequestAborted
        );

        return NoContent();
    }

    /// <summary>
    /// Remove a food from favorites.
    /// </summary>
    [HttpDelete("{foodId}/favorite")]
    [RemoteCommand(Invalidates = ["GetFavorites"])]
    [Authorize]
    public async Task<ActionResult> RemoveFavorite(string foodId)
    {
        var userId = ResolveUserId();

        var food = await ResolveFoodEntityAsync(foodId, HttpContext.RequestAborted);
        if (food == null)
        {
            return NotFound();
        }

        await _favoriteService.RemoveFavoriteAsync(
            userId,
            food.Id,
            HttpContext.RequestAborted
        );

        return NoContent();
    }

    /// <summary>
    /// Get recently used foods (excluding favorites).
    /// </summary>
    [HttpGet("recent")]
    [RemoteQuery]
    [Authorize]
    public async Task<ActionResult<Food[]>> GetRecentFoods([FromQuery] int limit = 20)
    {
        var userId = ResolveUserId();

        var foods = await _favoriteService.GetRecentFoodsAsync(
            userId,
            limit,
            HttpContext.RequestAborted
        );

        return Ok(foods.ToArray());
    }

    /// <summary>
    /// Get how many meal attributions reference a specific food.
    /// </summary>
    [HttpGet("{foodId}/attribution-count")]
    [RemoteQuery]
    [Authorize]
    [ProducesResponseType(typeof(FoodAttributionCount), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FoodAttributionCount>> GetFoodAttributionCount(string foodId)
    {
        var food = await ResolveFoodEntityAsync(foodId, HttpContext.RequestAborted);
        if (food == null)
        {
            return NotFound();
        }

        var count = await _treatmentFoodService.CountByFoodIdAsync(
            food.Id,
            HttpContext.RequestAborted
        );

        return Ok(new FoodAttributionCount
        {
            FoodId = foodId,
            Count = count,
        });
    }

    /// <summary>
    /// Delete a food from the database, handling any meal attributions that reference it.
    /// </summary>
    /// <param name="foodId">The food ID to delete.</param>
    /// <param name="attributionMode">How to handle existing attributions: "clear" (default) sets them to Other, "remove" deletes them.</param>
    [HttpDelete("{foodId}")]
    [RemoteCommand(Invalidates = ["GetFavorites", "GetRecentFoods"])]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteFood(
        string foodId,
        [FromQuery] string attributionMode = "clear")
    {
        var food = await ResolveFoodEntityAsync(foodId, HttpContext.RequestAborted);
        if (food == null)
        {
            return NotFound();
        }

        // Handle attributions before deleting the food
        if (attributionMode == "remove")
        {
            await _treatmentFoodService.DeleteByFoodIdAsync(
                food.Id,
                HttpContext.RequestAborted
            );
        }
        else
        {
            await _treatmentFoodService.ClearFoodReferencesByFoodIdAsync(
                food.Id,
                HttpContext.RequestAborted
            );
        }

        // Delete the food itself
        var id = food.OriginalId ?? food.Id.ToString();
        await _foodService.DeleteFoodAsync(id, HttpContext.RequestAborted);

        return NoContent();
    }

    private string ResolveUserId()
    {
        return HttpContext.GetSubjectIdString() ?? DefaultUserId;
    }

    private async Task<FoodEntity?> ResolveFoodEntityAsync(
        string id,
        CancellationToken cancellationToken
    )
    {
        var entity = await _context
            .Foods.AsNoTracking()
            .FirstOrDefaultAsync(f => f.OriginalId == id, cancellationToken);

        if (entity != null)
        {
            return entity;
        }

        return Guid.TryParse(id, out var guid)
            ? await _context.Foods.AsNoTracking().FirstOrDefaultAsync(
                f => f.Id == guid,
                cancellationToken
            )
            : null;
    }
}
