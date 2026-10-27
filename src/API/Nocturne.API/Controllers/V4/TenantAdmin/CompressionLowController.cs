using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.TenantAdmin;

/// <summary>
/// Controller for compression low detection and review.
/// </summary>
/// <remarks>
/// Every write here is the glucose category and requires <see cref="Scope.GlucoseReadWrite"/>.
/// Accepting a suggestion writes a <see cref="StateSpanCategory.DataExclusion"/> span
/// (<c>CompressionLowService.ApplyBatchAsync</c>) marking the flagged readings, which is the
/// glucose category for the reason <c>StateSpanWriteScopeGuard</c> gives, and dismiss, delete and
/// detection all write the suggestions that propose one. Each write therefore carries its own
/// <see cref="Scope.GlucoseReadWrite"/> requirement, and the class-level gate gives the reads
/// the matching <see cref="Scope.GlucoseRead"/>.
/// <para>
/// The gate is <see cref="RequireScopeAttribute"/> and not <c>[Authorize]</c> because the data
/// quality report reads these suggestions: a public share is deliberately
/// <c>IsAuthenticated: false</c>, so <c>[Authorize]</c> 401s the report for every share whatever
/// the tenant granted. The <c>compression_low_suggestions</c> table has no
/// <see cref="ShareDataCategories"/> entry, so a share's rows are hidden by RLS and it reads an
/// empty list rather than an error.
/// </para>
/// </remarks>
/// <seealso cref="ICompressionLowService"/>
/// <seealso cref="ICompressionLowDetectionService"/>
[ApiController]
[Tags("TenantAdmin")]
[Route("api/v4/compression-lows")]
[RequireScope(Scope.GlucoseRead)]
public class CompressionLowController : ControllerBase
{
    /// <summary>
    /// Ceiling on one review. Each item is read twice and written up to four times, each on its own
    /// context, so the shared bulk ceiling of 1000 would hold a request for too long.
    /// </summary>
    private const int MaxBatchItems = 200;

    private readonly ICompressionLowService _compressionLowService;
    private readonly ICompressionLowDetectionService _detectionService;

    /// <summary>
    /// What a read tells the reader when the tenant's settings could not be read. The condition is
    /// transient, so the page is worth retrying. <see cref="SettingsUnavailableException"/>.
    /// </summary>
    private const string SettingsUnavailable =
        "Your settings could not be read just now, so this cannot be shown. Try again in a moment.";

    /// <summary>
    /// What reviewing tells the reader when a suggestion has already been acted on or removed. The
    /// service says which id and which state, which is of no use to them.
    /// </summary>
    private const string SuggestionUnavailable =
        "That suggestion is no longer waiting for a decision. Refresh the page to see the current list.";

    /// <summary>
    /// Initializes a new instance of <see cref="CompressionLowController"/>.
    /// </summary>
    /// <param name="compressionLowService">Service for suggestion CRUD and accept/dismiss operations.</param>
    /// <param name="detectionService">Service for running compression low detection algorithms.</param>
    public CompressionLowController(
        ICompressionLowService compressionLowService,
        ICompressionLowDetectionService detectionService)
    {
        _compressionLowService = compressionLowService;
        _detectionService = detectionService;
    }

    /// <summary>
    /// Get compression low suggestions with optional filtering
    /// </summary>
    [HttpGet("suggestions")]
    [RemoteQuery]
    public async Task<ActionResult<IEnumerable<CompressionLowSuggestion>>> GetSuggestions(
        [FromQuery] CompressionLowStatus? status = null,
        [FromQuery] string? nightOf = null,
        CancellationToken cancellationToken = default)
    {
        DateOnly? nightOfDate = null;
        if (!string.IsNullOrEmpty(nightOf) && DateOnly.TryParse(nightOf, out var parsed))
            nightOfDate = parsed;

        var suggestions = await _compressionLowService.GetSuggestionsAsync(
            status, nightOfDate, cancellationToken);
        return Ok(suggestions);
    }

    /// <summary>
    /// Get a single suggestion with glucose entries for charting
    /// </summary>
    [HttpGet("suggestions/{id:guid}")]
    [RemoteQuery]
    public async Task<ActionResult<CompressionLowSuggestionWithEntries>> GetSuggestion(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var suggestion = await _compressionLowService.GetSuggestionWithEntriesAsync(id, cancellationToken);
            if (suggestion == null)
                return NotFound();
            return Ok(suggestion);
        }
        catch (SettingsUnavailableException)
        {
            return Problem(
                detail: SettingsUnavailable,
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Settings Unavailable");
        }
    }

    /// <summary>
    /// Accept, dismiss or delete several suggestions at once
    /// </summary>
    /// <remarks>
    /// Not one database commit: <see cref="ICompressionLowService.ApplyBatchAsync"/> says what a
    /// refused or failed request leaves behind.
    /// </remarks>
    [HttpPost("suggestions/review")]
    [RequireScope(Scope.GlucoseReadWrite)]
    [RemoteCommand(Invalidates = ["GetSuggestions"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ReviewSuggestions(
        [FromBody] CompressionLowBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Action is not { } action || !Enum.IsDefined(action))
            return Problem(detail: "Action must be Accept, Dismiss or Delete", statusCode: 400, title: "Bad Request");

        if (this.ValidateBulkSize(request.Items, "Suggestion", "suggestions", MaxBatchItems) is { } invalid)
            return invalid;

        try
        {
            await _compressionLowService.ApplyBatchAsync(
                action, request.Items, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException)
        {
            return Problem(detail: SuggestionUnavailable, statusCode: 400);
        }
    }

    /// <summary>
    /// Manually trigger detection for a date range (for testing)
    /// </summary>
    /// <remarks>
    /// Provide either a single `nightOf` date or a range with `startDate` and `endDate`.
    /// When using a range, detection runs for each night in the range (inclusive).
    /// Each night analysed persists the <c>compression_low_suggestions</c> rows it finds
    /// (<c>CompressionLowDetectionService.DetectForNightAsync</c>), so this is a write.
    /// </remarks>
    [HttpPost("detect")]
    [RequireScope(Scope.GlucoseReadWrite)]
    [RemoteCommand(Invalidates = ["GetSuggestions"])]
    [ProducesResponseType(typeof(DetectionResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<DetectionResult>> TriggerDetection(
        [FromBody] TriggerDetectionRequest request,
        CancellationToken cancellationToken = default)
    {
        // Single night mode (backward compatible)
        if (!string.IsNullOrEmpty(request.NightOf))
        {
            if (!DateOnly.TryParse(request.NightOf, out var nightOf))
                return Problem(detail: "Invalid date format. Use yyyy-MM-dd", statusCode: 400, title: "Bad Request");

            var count = await _detectionService.DetectForNightAsync(nightOf, cancellationToken);
            return Ok(new DetectionResult
            {
                TotalSuggestionsCreated = count,
                NightsProcessed = 1,
                Results = [new NightDetectionResult { NightOf = nightOf.ToString("yyyy-MM-dd"), SuggestionsCreated = count }]
            });
        }

        // Date range mode
        if (string.IsNullOrEmpty(request.StartDate) || string.IsNullOrEmpty(request.EndDate))
            return Problem(detail: "Provide either 'nightOf' for a single night or 'startDate' and 'endDate' for a range", statusCode: 400, title: "Bad Request");

        if (!DateOnly.TryParse(request.StartDate, out var startDate))
            return Problem(detail: "Invalid startDate format. Use yyyy-MM-dd", statusCode: 400, title: "Bad Request");

        if (!DateOnly.TryParse(request.EndDate, out var endDate))
            return Problem(detail: "Invalid endDate format. Use yyyy-MM-dd", statusCode: 400, title: "Bad Request");

        if (endDate < startDate)
            return Problem(detail: "endDate must be >= startDate", statusCode: 400, title: "Bad Request");

        var dayCount = endDate.DayNumber - startDate.DayNumber + 1;
        var results = new List<NightDetectionResult>();
        var totalCount = 0;

        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            var count = await _detectionService.DetectForNightAsync(date, cancellationToken);
            results.Add(new NightDetectionResult { NightOf = date.ToString("yyyy-MM-dd"), SuggestionsCreated = count });
            totalCount += count;
        }

        return Ok(new DetectionResult
        {
            TotalSuggestionsCreated = totalCount,
            NightsProcessed = dayCount,
            Results = results
        });
    }
}
