using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for compression low detection and review
/// </summary>
[ApiController]
[Route("api/v4/compression-lows")]
[Authorize]
public class CompressionLowController : ControllerBase
{
    private readonly ICompressionLowService _compressionLowService;
    private readonly ICompressionLowDetectionService _detectionService;

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
    public async Task<ActionResult<CompressionLowSuggestionWithEntries>> GetSuggestion(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var suggestion = await _compressionLowService.GetSuggestionWithEntriesAsync(id, cancellationToken);
        if (suggestion == null)
            return NotFound();
        return Ok(suggestion);
    }

    /// <summary>
    /// Accept a suggestion with adjusted bounds
    /// </summary>
    [HttpPost("suggestions/{id:guid}/accept")]
    [ProducesResponseType(typeof(StateSpan), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StateSpan>> AcceptSuggestion(
        Guid id,
        [FromBody] AcceptSuggestionRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var stateSpan = await _compressionLowService.AcceptSuggestionAsync(
                id, request.StartMills, request.EndMills, cancellationToken);
            return Ok(stateSpan);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Dismiss a suggestion
    /// </summary>
    [HttpPost("suggestions/{id:guid}/dismiss")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DismissSuggestion(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _compressionLowService.DismissSuggestionAsync(id, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete a suggestion and its associated state span
    /// </summary>
    [HttpDelete("suggestions/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteSuggestion(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _compressionLowService.DeleteSuggestionAsync(id, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Manually trigger detection for a date range (for testing)
    /// </summary>
    /// <remarks>
    /// Provide either a single `nightOf` date or a range with `startDate` and `endDate`.
    /// When using a range, detection runs for each night in the range (inclusive).
    /// </remarks>
    [HttpPost("detect")]
    [ProducesResponseType(typeof(DetectionResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<DetectionResult>> TriggerDetection(
        [FromBody] TriggerDetectionRequest request,
        CancellationToken cancellationToken = default)
    {
        // Single night mode (backward compatible)
        if (!string.IsNullOrEmpty(request.NightOf))
        {
            if (!DateOnly.TryParse(request.NightOf, out var nightOf))
                return BadRequest(new { error = "Invalid date format. Use yyyy-MM-dd" });

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
            return BadRequest(new { error = "Provide either 'nightOf' for a single night or 'startDate' and 'endDate' for a range" });

        if (!DateOnly.TryParse(request.StartDate, out var startDate))
            return BadRequest(new { error = "Invalid startDate format. Use yyyy-MM-dd" });

        if (!DateOnly.TryParse(request.EndDate, out var endDate))
            return BadRequest(new { error = "Invalid endDate format. Use yyyy-MM-dd" });

        if (endDate < startDate)
            return BadRequest(new { error = "endDate must be >= startDate" });

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
