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
/// Controller for food favorites and recent foods.
/// </summary>
[ApiController]
[Route("api/v4/foods")]
[Tags("V4 Foods")]
[ClientPropertyName("foodsV4")]
public class FoodsController : ControllerBase
{
    private readonly NocturneDbContext _context;
    private readonly IUserFoodFavoriteService _favoriteService;

    public FoodsController(NocturneDbContext context, IUserFoodFavoriteService favoriteService)
    {
        _context = context;
        _favoriteService = favoriteService;
    }

    /// <summary>
    /// Get current user's favorite foods.
    /// </summary>
    [HttpGet("favorites")]
    [Authorize]
    public async Task<ActionResult<Food[]>> GetFavorites()
    {
        var userId = HttpContext.GetSubjectIdString();
        if (string.IsNullOrEmpty(userId))
        {
            return Forbid();
        }

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
    [Authorize]
    public async Task<ActionResult> AddFavorite(string foodId)
    {
        var userId = HttpContext.GetSubjectIdString();
        if (string.IsNullOrEmpty(userId))
        {
            return Forbid();
        }

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
    [Authorize]
    public async Task<ActionResult> RemoveFavorite(string foodId)
    {
        var userId = HttpContext.GetSubjectIdString();
        if (string.IsNullOrEmpty(userId))
        {
            return Forbid();
        }

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
    [Authorize]
    public async Task<ActionResult<Food[]>> GetRecentFoods([FromQuery] int limit = 20)
    {
        var userId = HttpContext.GetSubjectIdString();
        if (string.IsNullOrEmpty(userId))
        {
            return Forbid();
        }

        var foods = await _favoriteService.GetRecentFoodsAsync(
            userId,
            limit,
            HttpContext.RequestAborted
        );

        return Ok(foods.ToArray());
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
