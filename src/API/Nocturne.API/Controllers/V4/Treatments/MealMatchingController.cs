using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Treatments;

/// <summary>
/// Suggested matches between imported food entries and carb intake records. Accept a suggested match to link
/// the food entry to a treatment, or dismiss it.
/// </summary>
/// <remarks>
/// Meal matching surfaces suggested pairings between raw connector food entries
/// (imported via <see cref="ConnectorFoodEntriesController"/>) and existing carb intake treatments.
/// Suggestions are scored by <see cref="IMealMatchingService.GetSuggestionsAsync"/> and presented
/// to the user for acceptance or dismissal.
///
/// <b>Accept</b> (<c>POST /accept</c>) — links a connector food entry to a treatment, archives the
/// associated notification (topic <c>meal_matching.suggested_match</c>) with reason <c>Completed</c>.
///
/// <b>Dismiss</b> (<c>POST /dismiss</c>) — marks the food entry as dismissed and archives the
/// notification with reason <c>Dismissed</c>.
///
/// Both mutation endpoints use <c>RemoteCommandAttribute</c> with
/// cache invalidation on <c>GetSuggestions</c>.
/// </remarks>
/// <seealso cref="IMealMatchingService"/>
/// <seealso cref="IConnectorFoodEntryRepository"/>
/// <seealso cref="IInAppNotificationService"/>
[ApiController]
[Tags("Treatments")]
[Route("api/v4/meal-matching")]
[Authorize]
public class MealMatchingController : ControllerBase
{
    private readonly IMealMatchingService _mealMatchingService;
    private readonly IMealMatchLinker _linker;
    private readonly IConnectorFoodEntryRepository _foodEntryRepository;
    private readonly IInAppNotificationService _notificationService;
    private readonly TimeProvider _time;

    public MealMatchingController(
        IMealMatchingService mealMatchingService,
        IMealMatchLinker linker,
        IConnectorFoodEntryRepository foodEntryRepository,
        IInAppNotificationService notificationService,
        TimeProvider time)
    {
        _mealMatchingService = mealMatchingService;
        _linker = linker;
        _foodEntryRepository = foodEntryRepository;
        _notificationService = notificationService;
        _time = time;
    }

    /// <summary>
    /// Get a food entry for review
    /// </summary>
    [HttpGet("food-entries/{id:guid}")]
    [RequireScope(Scope.FoodRead)]
    [RemoteQuery]
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
    [RequireScope(true, Scope.FoodRead, Scope.TreatmentsRead)]
    [RemoteQuery]
    [ProducesResponseType(typeof(SuggestedMealMatch[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SuggestedMealMatch[]>> GetSuggestions(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to)
    {
        var now = _time.GetUtcNow();
        var fromDate = from ?? now.AddDays(-1);
        var toDate = to ?? now;

        IReadOnlyList<SuggestedMealMatchResult> suggestions;
        try
        {
            suggestions = await _mealMatchingService.GetSuggestionsAsync(
                fromDate,
                toDate,
                HttpContext.RequestAborted);
        }
        catch (SettingsUnavailableException)
        {
            return Problem(
                detail: "Your matching settings could not be read just now, so no suggestions can be offered. Try again in a moment.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Settings Unavailable");
        }

        var result = suggestions.Select(s => new SuggestedMealMatch
        {
            FoodEntryId = s.FoodEntryId,
            FoodName = s.FoodName,
            MealName = s.MealName,
            Carbs = s.Carbs,
            ConsumedAt = s.ConsumedAt,
            CarbIntakeId = s.CarbIntakeId,
            CarbIntakeCarbs = s.CarbIntakeCarbs,
            CarbIntakeMills = s.CarbIntakeMills,
            MatchScore = s.MatchScore,
        }).ToArray();

        return Ok(result);
    }

    /// <summary>
    /// Accept a meal match
    /// Links the connector food entry to the carb intake and marks the entry matched. Requires the treatments read-write scope.
    /// </summary>
    /// <remarks>
    /// Writes a <c>treatment_foods</c> row keyed by the carb intake, which
    /// <see cref="NutritionController"/> gates on <c>treatments.readwrite</c>, and marks the
    /// connector food entry matched. Gated on the treatments category rather than food because the
    /// carb breakdown is a COB input. A repeat of an accept that linked answers 204 and writes
    /// nothing; an entry matched to another carb intake, dismissed or withdrawn answers 409.
    /// </remarks>
    [HttpPost("accept")]
    [RequireScope(Scope.TreatmentsReadWrite)]
    [RemoteCommand(Invalidates = ["GetSuggestions", "Nutrition_GetMeals"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ErrorEnvelope]
    public async Task<ActionResult> AcceptMatch([FromBody] AcceptMatchRequest request)
    {
        var userId = HttpContext.GetSubjectIdString();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var outcome = await _linker.LinkAsync(
            request.FoodEntryId,
            request.CarbIntakeId,
            request.Carbs,
            request.TimeOffsetMinutes,
            HttpContext.RequestAborted);

        ActionResult? refusal = outcome switch
        {
            AcceptMatchOutcome.Linked or AcceptMatchOutcome.AlreadyLinked => null,
            AcceptMatchOutcome.NotFound => Problem(
                detail: "This food or this carb entry no longer exists. Nothing changed.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Match Not Found"),
            AcceptMatchOutcome.AlreadyResolved => Problem(
                detail: "This food is already linked to a different carb entry, or it was dismissed or removed. Nothing changed.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Food Cannot Be Linked"),
            _ => throw new InvalidOperationException($"Unhandled accept outcome {outcome}."),
        };

        if (refusal is not null)
        {
            return refusal;
        }

        await _notificationService.ArchiveBySourceAsync(
            userId,
            IMealMatchingService.SuggestedMatchNotificationType,
            request.FoodEntryId.ToString(),
            NotificationArchiveReason.Completed,
            HttpContext.RequestAborted);

        return NoContent();
    }

    /// <summary>
    /// Dismiss a meal match
    /// Requires the food read-write scope.
    /// </summary>
    /// <remarks>
    /// Writes only the <c>connector_food_entries</c> status, which is the food category in
    /// <see cref="ShareDataCategories.GovernedTables"/>, so this action is gated on the food
    /// category while <see cref="AcceptMatch"/> is gated on treatments. A repeat of a dismiss
    /// answers 204; a food entry that does not exist answers 404, and one already matched or
    /// withdrawn answers 409. Both leave the suggestion notification alone.
    /// </remarks>
    [HttpPost("dismiss")]
    [RequireScope(Scope.FoodReadWrite)]
    [RemoteCommand(Invalidates = ["GetSuggestions"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ErrorEnvelope]
    public async Task<ActionResult> DismissMatch([FromBody] DismissMatchRequest request)
    {
        var userId = HttpContext.GetSubjectIdString();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var outcome = await _mealMatchingService.DismissMatchAsync(request.FoodEntryId, HttpContext.RequestAborted);

        ActionResult? refusal = outcome switch
        {
            DismissMatchOutcome.Dismissed or DismissMatchOutcome.AlreadyDismissed => null,
            DismissMatchOutcome.NotFound => Problem(
                detail: "This food no longer exists. Nothing changed.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Food Not Found"),
            DismissMatchOutcome.AlreadyResolved => Problem(
                detail: "This food is already linked to a carb entry, or it was removed. Nothing changed.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Food Cannot Be Dismissed"),
            _ => throw new InvalidOperationException($"Unhandled dismiss outcome {outcome}."),
        };

        if (refusal is not null)
        {
            return refusal;
        }

        await _notificationService.ArchiveBySourceAsync(
            userId,
            IMealMatchingService.SuggestedMatchNotificationType,
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

    /// <summary>The carb intake to link the food to, from <see cref="SuggestedMealMatch.CarbIntakeId"/>.</summary>
    public Guid CarbIntakeId { get; set; }

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
/// A suggested meal match between a food entry and a carb intake
/// </summary>
public class SuggestedMealMatch
{
    public Guid FoodEntryId { get; set; }
    public string? FoodName { get; set; }
    public string? MealName { get; set; }
    public decimal Carbs { get; set; }
    public DateTimeOffset ConsumedAt { get; set; }
    public Guid CarbIntakeId { get; set; }
    public decimal CarbIntakeCarbs { get; set; }
    public long CarbIntakeMills { get; set; }
    public double MatchScore { get; set; }
}
