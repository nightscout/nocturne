using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// The Loop Analysis report: every page is computed server-side from one fetch per request and
/// returned as <c>{ primary, comparison? }</c> inside the <see cref="LoopAnalysisResponse"/>
/// envelope.
/// </summary>
/// <remarks>
/// Every action binds a <see cref="LoopAnalysisQuery"/>, resolves it with <see cref="TryResolveAsync"/>
/// and fills the envelope with <see cref="LoopAnalysisRequest.Fill"/>. Responses carry a 60-second
/// client-only cache and nothing is cached server-side, because
/// <see cref="LoopAnalysisReadScopeGuard"/> narrows each response to its caller.
/// </remarks>
/// <seealso cref="ILoopAnalysisService"/>
[ApiController]
[Tags("Analytics")]
[Route("api/v4/loop-analysis")]
[Produces("application/json")]
public class LoopAnalysisController(
    ILoopAnalysisService service,
    TenantClockResolver tenantClock,
    TimeProvider timeProvider) : ControllerBase
{
    private readonly LoopAnalysisRequestResolver _resolver = new(tenantClock, timeProvider);

    /// <summary>
    /// Every series in 288 local five-minute slots of the day: glucose, scheduled and delivered
    /// basal, net loop correction and its components, IOB/COB, manual boluses, carbs and the
    /// target range, with the days behind them.
    /// </summary>
    /// <param name="query">The range, comparison and masks (<see cref="LoopAnalysisQuery"/>).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    [HttpGet("daily-profile")]
    [RemoteQuery]
    [RequireScope(LoopAnalysisReadScopeGuard.AdmissionScope)]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client)]
    [ProducesResponseType(typeof(DailyProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<ActionResult<DailyProfileResponse>> GetDailyProfile(
        [FromQuery] LoopAnalysisQuery query,
        CancellationToken cancellationToken = default) =>
        ServeAsync(query, service.GetDailyProfileAsync, cancellationToken);

    /// <summary>
    /// How far each prediction curve's values at +30, +60, +90 and +120 minutes landed from the
    /// sensor reading nearest the time they predicted, overall, by local hour of day and by context.
    /// Error is predicted minus actual, so a positive bias is a prediction that ran high. A horizon
    /// with no reading within 2.5 minutes is counted as unmatched. Without glucose.read and
    /// devices.read no curve is returned; without treatments.read the context breakdowns and the oref IOB, zero-temp and
    /// COB curves are withheld.
    /// </summary>
    /// <remarks>
    /// Only the +30 and +60 minute bias is a sign of the settings a prediction ran on: the reading at
    /// a longer horizon follows everything the loop did after the prediction, which the prediction
    /// assumed would not happen. Error is predicted minus actual, so a positive bias is a prediction
    /// that ran high. A horizon with no reading within 2.5 minutes is counted as unmatched, never
    /// paired with an imputed value. Needs glucose.read and devices.read for any curve, the
    /// readings and the APS snapshots the curves come from; without treatments.read the context
    /// breakdowns are withheld, and so are oref's IOB, zero-temp and COB curves, each reported only
    /// in the contexts the algorithm relies on it in.
    /// </remarks>
    /// <param name="query">The range, comparison and masks (<see cref="LoopAnalysisQuery"/>).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    [HttpGet("prediction-accuracy")]
    [RemoteQuery]
    [RequireScope(LoopAnalysisReadScopeGuard.AdmissionScope)]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client)]
    [ProducesResponseType(typeof(PredictionAccuracyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PredictionAccuracyResponse>> GetPredictionAccuracy(
        [FromQuery] LoopAnalysisQuery query,
        CancellationToken cancellationToken = default)
    {
        var served = await ServeAsync(query, service.GetPredictionAccuracyAsync, cancellationToken);
        if (served.Result is OkObjectResult { Value: PredictionAccuracyResponse report })
            report.ContextLookbackMs = PredictionAccuracyCalculator.ContextLookbackMs;
        return served;
    }

    /// <summary>
    /// How the loop ran, each figure for the range and by local hour of the day: closed-loop, pump
    /// and CGM time, gaps between loop cycles and in the pump's delivery records, zero delivery by
    /// origin, time at the effective maximum basal rate and which cap bound it, time at oref's
    /// <c>max_iob</c>, automatic boluses, suggestions not enacted and the sensitivity ratio.
    /// </summary>
    /// <param name="query">The range, comparison and masks (<see cref="LoopAnalysisQuery"/>).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    [HttpGet("loop-health")]
    [RemoteQuery]
    [RequireScope(LoopAnalysisReadScopeGuard.AdmissionScope)]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client)]
    [ProducesResponseType(typeof(LoopHealthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LoopHealthResponse>> GetLoopHealth(
        [FromQuery] LoopAnalysisQuery query,
        CancellationToken cancellationToken = default)
    {
        var served = await ServeAsync(query, service.GetLoopHealthAsync, cancellationToken);
        if (served.Result is OkObjectResult { Value: LoopHealthResponse report })
            report.LoopGapMinutes = (int)(LoopHealthCalculator.LoopGapMs / LocalSlotBinning.MinuteMs);
        return served;
    }

    /// <summary>
    /// The basal need, ISF and carb ratio the user's own data shows, beside the profile's: basal per
    /// two-hour local block from fasting blocks, ISF from isolated correction boluses and carb ratio
    /// from isolated announced meals, per local time-of-day block. Each figure carries its n, spread,
    /// confidence and the reasons it is not higher, the episodes rejected by each rule, why it has no
    /// value where it has none, and for the primary range the episodes it was read from. These are
    /// observations, never instructions. Without glucose.read, treatments.read and therapy.read, or
    /// for a history-clamped caller, nothing is computed: <c>primary</c> and <c>comparison</c> are
    /// null and <c>withheldReason</c> says why.
    /// </summary>
    /// <remarks>
    /// The page is withheld whole, with nothing computed, <c>primary</c> and <c>comparison</c> null
    /// and the reason in <c>withheldReason</c>: without glucose.read, treatments.read and
    /// therapy.read, since every figure reads all three (and <c>withheldSeries</c> then holds
    /// <c>SettingsEvidence</c>), and for a history-clamped caller, since every rule looks back hours
    /// before its window and would see nothing before the clamp. <c>rules</c> gives the thresholds
    /// the rules apply.
    /// </remarks>
    /// <param name="query">The range, comparison and masks (<see cref="LoopAnalysisQuery"/>).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    [HttpGet("settings-evidence")]
    [RemoteQuery]
    [RequireScope(LoopAnalysisReadScopeGuard.AdmissionScope)]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client)]
    [ProducesResponseType(typeof(SettingsEvidenceReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SettingsEvidenceReport>> GetSettingsEvidence(
        [FromQuery] LoopAnalysisQuery query,
        CancellationToken cancellationToken = default)
    {
        SettingsEvidenceWithheldReason? withheld =
            LoopAnalysisReadScopeGuard.WithheldSettingsEvidence(HttpContext.GetGrantedScopes()).Count > 0
                ? SettingsEvidenceWithheldReason.ReadScopes
                : HttpContext.IsCallerHistoryClamped() ? SettingsEvidenceWithheldReason.HistoryClamped : null;

        var served = await ServeAsync<SettingsEvidenceReport>(
            query,
            withheld is null
                ? service.GetSettingsEvidenceAsync
                : (filter, _, ct) => EnvelopeOnlyAsync(new SettingsEvidenceReport(), filter, ct),
            cancellationToken);
        if (served.Result is OkObjectResult { Value: SettingsEvidenceReport report })
        {
            report.WithheldReason = withheld;
            report.Rules = SettingsEvidenceCalculator.Rules();
        }
        return served;
    }

    /// <summary>
    /// What every Loop Analysis page shares about a request, without any page's figures: the
    /// resolved ranges, the filters applied and the ones the caller cannot use, the history clamp,
    /// the time-zone state and the profiles the <c>profileName</c> filter can be set to.
    /// </summary>
    /// <param name="query">The range, comparison and masks (<see cref="LoopAnalysisQuery"/>).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    [HttpGet("envelope")]
    [RemoteQuery]
    [RequireScope(LoopAnalysisReadScopeGuard.AdmissionScope)]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client)]
    [ProducesResponseType(typeof(LoopAnalysisResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LoopAnalysisResponse>> GetEnvelope(
        [FromQuery] LoopAnalysisQuery query,
        CancellationToken cancellationToken = default)
    {
        var (invalid, request) = await TryResolveAsync(query, cancellationToken);
        if (invalid is not null)
            return invalid;

        var response = new LoopAnalysisResponse();
        if (request!.Zone is not null && (request.Filter ?? request.HistoryWindowFilter) is { } filter)
            response = await EnvelopeOnlyAsync(response, filter, cancellationToken);
        return Ok(LoopAnalysisReadScopeGuard.Redact(request.Fill(response), HttpContext.GetGrantedScopes()));
    }

    /// <summary>
    /// Glucose, net loop correction, net insulin above schedule, IOB and COB aligned on up to three
    /// cohorts of events (meals, correction boluses, unannounced rises, and the starts of sensors,
    /// exercise, overrides and temporary targets), each spread across its events in five-minute
    /// slots from the anchor, with each event's figures and their spread. An event with no reading
    /// within 5 minutes of its anchor is dropped and counted. A cohort whose anchor or glucose read
    /// scope the caller lacks is dropped and its <c>anchor{n}</c> reported as ignored; without
    /// treatments.read the event list and every insulin, IOB and COB series are withheld. Equal start
    /// and end times of day mean all day, and grams given the wrong way round are swapped.
    /// </summary>
    /// <remarks>
    /// An event is dropped and counted when no reading lies within 5 minutes of its anchor. Every
    /// cohort needs glucose.read, and the read scope its anchor is drawn from; one the caller lacks
    /// them for is dropped before anything is computed and its <c>anchor{n}</c> reported as ignored.
    /// Without treatments.read the event list and every insulin, IOB and COB series and figure are
    /// withheld. Equal start and end times of day mean all day, and grams given the wrong way round
    /// are swapped.
    /// </remarks>
    /// <param name="query">The range, comparison and masks (<see cref="LoopAnalysisQuery"/>).</param>
    /// <param name="events">The cohorts and window (<see cref="EventResponseQuery"/>).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    [HttpGet("event-response")]
    [RemoteQuery]
    [RequireScope(LoopAnalysisReadScopeGuard.AdmissionScope)]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client)]
    [ProducesResponseType(typeof(EventResponseReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EventResponseReport>> GetEventResponse(
        [FromQuery] LoopAnalysisQuery query,
        [FromQuery] EventResponseQuery events,
        CancellationToken cancellationToken = default)
    {
        if (events.Problem() is { } problem)
            return BadRange(problem);

        var granted = HttpContext.GetGrantedScopes();
        var definition = events.ToDefinition();
        var ignored = LoopAnalysisReadScopeGuard.NarrowEvents(definition, granted);
        var passed = Request.Query.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var served = await ServeAsync(
            query,
            (filter, zone, ct) => service.GetEventResponseAsync(filter, definition, zone, ct),
            cancellationToken);
        if (served.Result is OkObjectResult { Value: EventResponseReport report })
        {
            report.Definition = definition;
            report.SlotMinutes = EventResponseCalculator.SlotMinutes(definition);
            report.BackToStartMgdl = EventEpisode.BackToStartMgdl;
            report.IgnoredFilters.AddRange(ignored.Where(passed.Contains).Concat(events.OutOfLimits()));
            report.UnavailableFilters.AddRange(LoopAnalysisReadScopeGuard.UnavailableEventFilters(granted));
            report.UnavailableAnchors = LoopAnalysisReadScopeGuard.UnavailableAnchors(granted);
        }
        return served;
    }

    /// <summary>
    /// Resolves <paramref name="query"/>, computes the page when there is a range to analyse, fills
    /// the envelope and narrows the result to the caller.
    /// </summary>
    private async Task<ActionResult<T>> ServeAsync<T>(
        LoopAnalysisQuery query,
        Func<LoopAnalysisFilter, TimeZoneInfo, CancellationToken, Task<T>> compute,
        CancellationToken ct)
        where T : LoopAnalysisResponse, new()
    {
        var (invalid, request) = await TryResolveAsync(query, ct);
        if (invalid is not null)
            return invalid;

        var result = request!.Filter is { } filter && request.Zone is { } zone ? await compute(filter, zone, ct) : new T();
        return Ok(LoopAnalysisReadScopeGuard.Redact(request.Fill(result), HttpContext.GetGrantedScopes()));
    }

    /// <summary><paramref name="response"/> with only the envelope's profile names computed.</summary>
    private async Task<T> EnvelopeOnlyAsync<T>(T response, LoopAnalysisFilter filter, CancellationToken ct)
        where T : LoopAnalysisResponse
    {
        if (filter.ReadProfileNames)
            response.ProfileNames = await service.GetProfileNamesAsync(filter, ct);
        return response;
    }

    /// <summary>
    /// Validates the query and resolves it for the caller with <see cref="LoopAnalysisRequestResolver"/>.
    /// </summary>
    private async Task<(ObjectResult? Invalid, LoopAnalysisRequest? Request)> TryResolveAsync(
        LoopAnalysisQuery query, CancellationToken ct)
    {
        if (LoopAnalysisRequestResolver.Problem(query) is { } problem)
            return (BadRange(problem), null);

        var request = await _resolver.ResolveAsync(
            query,
            HttpContext.GetGrantedScopes(),
            HttpContext.IsCallerHistoryClamped(),
            Request.Query.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            ct);
        return (null, request);
    }

    private ObjectResult BadRange(string detail) => Problem(detail: detail, statusCode: 400, title: "Bad Request");
}
