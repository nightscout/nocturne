using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for meal matching operations
/// </summary>
[ApiController]
[Route("api/v4/meal-matching")]
[Tags("V4 Meal Matching")]
[Authorize]
public class MealMatchingController : ControllerBase
{
    private readonly IMealMatchingService _mealMatchingService;
    private readonly IConnectorFoodEntryRepository _foodEntryRepository;
    private readonly IInAppNotificationService _notificationService;
    private readonly ILogger<MealMatchingController> _logger;

    public MealMatchingController(
        IMealMatchingService mealMatchingService,
        IConnectorFoodEntryRepository foodEntryRepository,
        IInAppNotificationService notificationService,
        ILogger<MealMatchingController> logger)
    {
        _mealMatchingService = mealMatchingService;
        _foodEntryRepository = foodEntryRepository;
        _notificationService = notificationService;
        _logger = logger;
    }

    /// <summary>
    /// Get a food entry for review
    /// </summary>
    [HttpGet("food-entries/{id:guid}")]
    [ProducesResponseType(typeof(ConnectorFoodEntry), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConnectorFoodEntry>> GetFoodEntry(Guid id)
    {
        var entry = await _foodEntryRepository.GetByIdAsync(id, HttpContext.RequestAborted);
        if (entry == null)
        {
            return NotFound();
        }
        return Ok(entry);
    }

    /// <summary>
    /// Get suggested meal matches for a date range
    /// </summary>
    [HttpGet("suggestions")]
    [ProducesResponseType(typeof(SuggestedMealMatch[]), StatusCodes.Status200OK)]
    public async Task<ActionResult<SuggestedMealMatch[]>> GetSuggestions(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to)
    {
        var fromDate = from ?? DateTimeOffset.UtcNow.AddDays(-1);
        var toDate = to ?? DateTimeOffset.UtcNow;

        var suggestions = await _mealMatchingService.GetSuggestionsAsync(
            fromDate,
            toDate,
            HttpContext.RequestAborted);

        var result = suggestions.Select(s => new SuggestedMealMatch
        {
            FoodEntryId = s.FoodEntryId,
            FoodName = s.FoodName,
            MealName = s.MealName,
            Carbs = s.Carbs,
            ConsumedAt = s.ConsumedAt,
            TreatmentId = s.TreatmentId,
            TreatmentCarbs = s.TreatmentCarbs,
            TreatmentMills = s.TreatmentMills,
            MatchScore = s.MatchScore,
        }).ToArray();

        return Ok(result);
    }

    /// <summary>
    /// Accept a meal match
    /// </summary>
    [HttpPost("accept")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> AcceptMatch([FromBody] AcceptMatchRequest request)
    {
        var userId = HttpContext.GetSubjectIdString();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        try
        {
            await _mealMatchingService.AcceptMatchAsync(
                request.FoodEntryId,
                request.TreatmentId,
                request.Carbs,
                request.TimeOffsetMinutes,
                HttpContext.RequestAborted);

            // Archive the notification
            await _notificationService.ArchiveBySourceAsync(
                userId,
                InAppNotificationType.SuggestedMealMatch,
                request.FoodEntryId.ToString(),
                NotificationArchiveReason.Completed,
                HttpContext.RequestAborted);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to accept meal match for food entry {FoodEntryId} and treatment {TreatmentId}",
                request.FoodEntryId, request.TreatmentId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Dismiss a meal match
    /// </summary>
    [HttpPost("dismiss")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DismissMatch([FromBody] DismissMatchRequest request)
    {
        var userId = HttpContext.GetSubjectIdString();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        await _mealMatchingService.DismissMatchAsync(request.FoodEntryId, HttpContext.RequestAborted);

        // Archive the notification
        await _notificationService.ArchiveBySourceAsync(
            userId,
            InAppNotificationType.SuggestedMealMatch,
            request.FoodEntryId.ToString(),
            NotificationArchiveReason.Dismissed,
            HttpContext.RequestAborted);

        return NoContent();
    }
}

/// <summary>
/// Request to accept a meal match
/// </summary>
public class AcceptMatchRequest
{
    public Guid FoodEntryId { get; set; }
    public Guid TreatmentId { get; set; }
    public decimal Carbs { get; set; }
    public int TimeOffsetMinutes { get; set; }
}

/// <summary>
/// Request to dismiss a meal match
/// </summary>
public class DismissMatchRequest
{
    public Guid FoodEntryId { get; set; }
}

/// <summary>
/// A suggested meal match between a food entry and treatment
/// </summary>
public class SuggestedMealMatch
{
    public Guid FoodEntryId { get; set; }
    public string? FoodName { get; set; }
    public string? MealName { get; set; }
    public decimal Carbs { get; set; }
    public DateTimeOffset ConsumedAt { get; set; }
    public Guid TreatmentId { get; set; }
    public decimal TreatmentCarbs { get; set; }
    public long TreatmentMills { get; set; }
    public double MatchScore { get; set; }
}
