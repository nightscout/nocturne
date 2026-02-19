using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for food favorites, recent foods, and food lifecycle management.
/// </summary>
[ApiController]
[Route("api/v4/foods")]
[Tags("V4 Foods")]
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
