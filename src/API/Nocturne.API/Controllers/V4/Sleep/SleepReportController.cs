using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Sleep.Report;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Sleep;

/// <summary>
/// On-demand sleep report endpoints: single-night detail and multi-night trends.
/// Both endpoints join CGM data with the session window at request time, so callers
/// need glucose.read in addition to sleep.read.
/// </summary>
/// <seealso cref="ISleepReportService"/>
[ApiController]
[Tags("Sleep")]
[Route("api/v4/sleep/report")]
[Authorize]
[RequireScope(requireAll: true, Scope.SleepRead, Scope.GlucoseRead)]
public class SleepReportController : ControllerBase
{
    private readonly ISleepReportService _service;

    public SleepReportController(ISleepReportService service)
    {
        _service = service;
    }

    /// <summary>
    /// Get the full single-night report for a sleep session, including stage breakdown,
    /// overnight TIR, hypo events, dawn phenomenon, and wake events.
    /// </summary>
    /// <param name="sessionId">The sleep session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("single-night/{sessionId:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(SleepSingleNightReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SleepSingleNightReport>> GetSingleNight(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var report = await _service.GetSingleNightReportAsync(sessionId, cancellationToken);
        if (report is null) return NotFound();
        return Ok(report);
    }

    /// <summary>
    /// Get the single-night report for the night that falls on a calendar date, resolving
    /// the date to a session via the same noon-rule bucketing and one-per-night deduplication
    /// the trends report uses. Lets reports deep-link a night by date rather than session id.
    /// </summary>
    /// <param name="date">The display-night date (YYYY-MM-DD).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("single-night/by-date/{date}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(SleepSingleNightReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SleepSingleNightReport>> GetSingleNightByDate(
        // Bound as a string rather than DateOnly: the generated TS client serializes
        // DateOnly route params via Date.toISOString(), which the string-typed remote
        // wrapper can't satisfy. A parsed string keeps the whole chain YYYY-MM-DD.
        string date,
        CancellationToken cancellationToken = default)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var displayDate))
            return Problem(
                detail: "Date must be in YYYY-MM-DD format.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Bad Request");

        var report = await _service.GetSingleNightReportByDateAsync(displayDate, cancellationToken);
        if (report is null) return NotFound();
        return Ok(report);
    }

    /// <summary>
    /// Get a multi-night trends report. Maximum date range is 90 days.
    /// When <paramref name="source"/> is omitted, sessions are deduplicated to one per
    /// calendar night (longest sleep wins; source priority as tie-breaker).
    /// </summary>
    /// <param name="from">Start of the date range (inclusive).</param>
    /// <param name="to">End of the date range (inclusive).</param>
    /// <param name="source">Optional source filter. When provided, deduplication is skipped.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("trends")]
    [RemoteQuery]
    [ProducesResponseType(typeof(SleepTrendsReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SleepTrendsReport>> GetTrends(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        [FromQuery] SleepSource? source = null,
        CancellationToken cancellationToken = default)
    {
        if ((to - from).TotalDays > 90)
            return Problem(
                detail: "Date range must not exceed 90 days.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Bad Request");

        // Query-bound dates arrive Kind=Unspecified when the client omits an offset;
        // Npgsql rejects those against timestamptz. Same normalization as StatisticsController.
        var fromUtc = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        var toUtc   = DateTime.SpecifyKind(to, DateTimeKind.Utc);

        var report = await _service.GetTrendsReportAsync(fromUtc, toUtc, source, cancellationToken);
        return Ok(report);
    }
}
