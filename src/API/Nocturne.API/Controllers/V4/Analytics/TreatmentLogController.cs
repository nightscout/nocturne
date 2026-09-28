using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Figures for the Treatment Log report.
/// </summary>
/// <remarks>
/// Every response is an aggregate (record counts and a treatment summary), so the controller sits
/// behind <see cref="Scope.ReportsRead"/> like <see cref="DataOverviewController"/>. The counts and
/// the search still expose the records themselves, so each kind is read only when the caller also
/// holds the category scope its list route requires; share RLS does not cover signed-in members.
/// </remarks>
/// <seealso cref="TreatmentLogFilter"/>
[ApiController]
[Tags("Analytics")]
[Route("api/v4/treatment-log")]
[Produces("application/json")]
[RequireScope(Scope.ReportsRead)]
[ClientPropertyName("treatmentLog")]
public class TreatmentLogController(
    IBolusRepository bolusRepository,
    ICarbIntakeRepository carbIntakeRepository,
    IBGCheckRepository bgCheckRepository,
    INoteRepository noteRepository,
    IDeviceEventRepository deviceEventRepository,
    IBasalInjectionRepository basalInjectionRepository,
    IStatisticsService statisticsService) : ControllerBase
{
    /// <summary>
    /// Records read per kind. Matches what the log's table asks each list route for, so the
    /// figures cover the same records the table shows.
    /// </summary>
    public const int RecordLimit = 10_000;

    /// <summary>
    /// Record counts per kind and the treatment summary over the records the log's filter keeps.
    /// </summary>
    /// <param name="from">Inclusive start of the range.</param>
    /// <param name="to">End of the range.</param>
    /// <param name="dayCount">Calendar days the range covers, for the summary's per-day averages.</param>
    /// <param name="category">Record kind to keep, or <see cref="TreatmentLogCategory.All"/>.</param>
    /// <param name="search">Free-text search; see <see cref="TreatmentLogFilter"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Never cached, per <see cref="Profiles.ProfileController.GetProfileSummary"/>: a just-entered
    /// record must count as soon as the table lists it.
    /// </remarks>
    [HttpGet("stats")]
    [RemoteQuery]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(TreatmentLogStats), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TreatmentLogStats>> GetStats(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        [FromQuery] int dayCount = 1,
        [FromQuery] TreatmentLogCategory category = TreatmentLogCategory.All,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        if (to <= from)
            return Problem(detail: "to must be after from.", statusCode: 400, title: "Bad Request");
        if (this.RejectDateSpan(from, to) is { } overlong)
            return overlong;
        if (dayCount < 1)
            return Problem(detail: "dayCount must be at least 1.", statusCode: 400, title: "Bad Request");

        var records = TreatmentLogFilter.Apply(
            await FetchAsync(from, to, HttpContext.GetGrantedScopes(), ct), category, search);

        return Ok(new TreatmentLogStats
        {
            Counts = TreatmentLogFilter.Count(records),
            TreatmentSummary = records.Boluses.Count > 0 || records.CarbIntakes.Count > 0
                ? statisticsService.CalculateTreatmentSummary(records.Boluses, records.CarbIntakes, dayCount: dayCount)
                : null,
        });
    }

    private async Task<TreatmentLogRecords> FetchAsync(
        DateTime from, DateTime to, IReadOnlySet<string> granted, CancellationToken ct)
    {
        var treatments = AnalyticsReadScopes.Allows(granted, Scope.TreatmentsRead);
        var devices = AnalyticsReadScopes.Allows(granted, Scope.DevicesRead);
        var glucose = AnalyticsReadScopes.Allows(granted, Scope.GlucoseRead);

        var boluses = treatments
            ? bolusRepository.GetAsync(from, to, null, null, RecordLimit, ct: ct)
            : Task.FromResult(Enumerable.Empty<Bolus>());
        var carbIntakes = treatments
            ? carbIntakeRepository.GetAsync(from, to, null, null, RecordLimit, ct: ct)
            : Task.FromResult(Enumerable.Empty<CarbIntake>());
        var bgChecks = glucose
            ? bgCheckRepository.GetAsync(from, to, null, null, RecordLimit, ct: ct)
            : Task.FromResult(Enumerable.Empty<BGCheck>());
        var notes = treatments
            ? noteRepository.GetAsync(from, to, null, null, RecordLimit, ct: ct)
            : Task.FromResult(Enumerable.Empty<Note>());
        var deviceEvents = devices
            ? deviceEventRepository.GetAsync(from, to, null, null, RecordLimit, ct: ct)
            : Task.FromResult(Enumerable.Empty<DeviceEvent>());
        var basalInjections = treatments
            ? basalInjectionRepository.GetAsync(from, to, null, null, RecordLimit, 0, descending: true, ct)
            : Task.FromResult(Enumerable.Empty<BasalInjection>());

        await Task.WhenAll(boluses, carbIntakes, bgChecks, notes, deviceEvents, basalInjections);

        return new TreatmentLogRecords(
            [.. await boluses],
            [.. await carbIntakes],
            [.. await bgChecks],
            [.. await notes],
            [.. await deviceEvents],
            [.. await basalInjections]);
    }
}
