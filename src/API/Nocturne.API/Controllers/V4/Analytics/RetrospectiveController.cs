using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
namespace Nocturne.API.Controllers.V4.Analytics;
/// <summary>
/// Controller providing retrospective (day-in-review) diabetes data.
/// Combines IOB, COB, interpolated glucose, and basal rate at any historical point in time.
/// </summary>
/// <remarks>
/// Three endpoints are exposed:
/// <list type="bullet">
///   <item><term>GET /at</term><description>Single point-in-time snapshot with IOB, COB, glucose, basal, and recent treatments.</description></item>
///   <item><term>GET /timeline</term><description>Full-day timeline sampled at a configurable interval (1-60 minutes).</description></item>
///   <item><term>GET /basal-timeline</term><description>Basal rate timeline showing scheduled vs. temp rates throughout a day.</description></item>
/// </list>
/// Glucose values are linearly interpolated from surrounding <see cref="Entry"/> records.
/// IOB is calculated via <see cref="IIobCalculator"/> and COB via <see cref="ICobCalculator"/>.
/// Scheduled basal falls back to <see cref="IBasalRateResolver.GetBasalRateAsync"/> when no temp basal
/// treatment is active.
/// </remarks>
/// <seealso cref="IIobCalculator"/>
/// <seealso cref="ICobCalculator"/>
/// <seealso cref="IEntryService"/>
/// <seealso cref="IBasalRateResolver"/>
[ApiController]
[Route("api/v4/[controller]")]
[Produces("application/json")]
public class RetrospectiveController : ControllerBase
{
    private readonly IIobCalculator _iobCalculator;
    private readonly ICobCalculator _cobCalculator;
    private readonly IEntryService _entryService;
    private readonly IBolusRepository _bolusRepository;
    private readonly ICarbIntakeRepository _carbIntakeRepository;
    private readonly ITempBasalRepository _tempBasalRepository;
    private readonly DeviceStatusProjectionService _projectionService;
    private readonly IBasalRateResolver _basalRateResolver;
    private readonly ILogger<RetrospectiveController> _logger;
    public RetrospectiveController(
        IIobCalculator iobCalculator,
        ICobCalculator cobCalculator,
        IEntryService entryService,
        IBolusRepository bolusRepository,
        ICarbIntakeRepository carbIntakeRepository,
        ITempBasalRepository tempBasalRepository,
        DeviceStatusProjectionService projectionService,
        IBasalRateResolver basalRateResolver,
        ILogger<RetrospectiveController> logger
    )
    {
        _iobCalculator = iobCalculator;
        _cobCalculator = cobCalculator;
        _entryService = entryService;
        _bolusRepository = bolusRepository;
        _carbIntakeRepository = carbIntakeRepository;
        _tempBasalRepository = tempBasalRepository;
        _projectionService = projectionService;
        _basalRateResolver = basalRateResolver;
        _logger = logger;
    }
    /// <summary>
    /// Get retrospective data at a specific point in time
    /// Returns IOB, COB, glucose, basal rate, and recent treatments
    /// </summary>
    /// <param name="time">Unix timestamp in milliseconds for the retrospective point</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Retrospective data at the specified time</returns>
    /// <response code="200">Returns the retrospective data</response>
    /// <response code="400">If the time parameter is invalid</response>
    /// <response code="500">If there was an internal server error</response>
    [HttpGet("at")]
    [RemoteQuery]
    [ProducesResponseType(typeof(RetrospectiveDataResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<RetrospectiveDataResponse>> GetRetrospectiveData(
        [FromQuery] long time,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (time <= 0)
            {
                return Problem(detail: "Time parameter must be a positive Unix timestamp in milliseconds", statusCode: 400, title: "Bad Request");
            }
            // Get glucose entries for context (entries around the target time)
            // Use JSON format for the findQuery with mills field
            var fromMills = time - (30 * 60 * 1000); // 30 minutes before
            var toMills = time + (5 * 60 * 1000);    // 5 minutes after
            var findQuery = $"{{\"mills\":{{\"$gte\":{fromMills},\"$lte\":{toMills}}}}}";
            var entries = await _entryService.GetEntriesWithAdvancedFilterAsync(
                type: "sgv",
                count: 50,
                skip: 0,
                findQuery: findQuery,
                dateString: null,
                reverseResults: false,
                cancellationToken: cancellationToken
            );
            var entryList = entries?.ToList() ?? new List<Entry>();
            _logger.LogDebug("GetRetrospectiveData: Found {Count} entries for time {Time} (range: {From} to {To})",
                entryList.Count, time, fromMills, toMills);
            // Query v4 types for IOB/COB calculation (8 hours before for DIA)
            var targetDateTime = DateTimeOffset.FromUnixTimeMilliseconds(time).UtcDateTime;
            var fetchFrom = targetDateTime.AddHours(-8);
            var boluses = (await _bolusRepository.GetAsync(
                from: fetchFrom, to: targetDateTime, device: null, source: null,
                limit: 2000, offset: 0, descending: false, ct: cancellationToken
            )).ToList();
            var tempBasals = (await _tempBasalRepository.GetAsync(
                from: fetchFrom, to: targetDateTime, device: null, source: null,
                limit: 2000, offset: 0, descending: false, ct: cancellationToken
            )).ToList();
            var carbIntakes = (await _carbIntakeRepository.GetAsync(
                from: fetchFrom, to: targetDateTime, device: null, source: null,
                limit: 2000, offset: 0, descending: false, ct: cancellationToken
            )).ToList();
            // Calculate IOB at the specified time
            var iobResult = await _iobCalculator.CalculateTotalAsync(
                boluses,
                tempBasals,
                time,
                ct: cancellationToken
            );
            // Calculate COB at the specified time
            var cobResult = await _cobCalculator.CalculateTotalAsync(
                carbIntakes,
                boluses,
                tempBasals,
                time,
                ct: cancellationToken
            );
            // Get glucose at the specified time (interpolated)
            var glucoseData = GetGlucoseAtTime(entryList, time);
            // Get basal rate at the specified time
            var basalData = await GetBasalRateAtTimeAsync(tempBasals, time);
            // Get recent treatments as summary data (boluses and carb intakes within 30 minutes)
            var recentTreatments = new List<TreatmentSummaryData>();
            var recentCutoff = time - 30 * 60 * 1000;
            foreach (var b in boluses.Where(b => b.Mills >= recentCutoff && b.Mills <= time).OrderByDescending(b => b.Mills).Take(10))
            {
                recentTreatments.Add(new TreatmentSummaryData
                {
                    Id = b.Id.ToString(),
                    Mills = b.Mills,
                    EventType = "Bolus",
                    Insulin = b.Insulin,
                });
            }
            foreach (var c in carbIntakes.Where(c => c.Mills >= recentCutoff && c.Mills <= time).OrderByDescending(c => c.Mills).Take(10))
            {
                recentTreatments.Add(new TreatmentSummaryData
                {
                    Id = c.Id.ToString(),
                    Mills = c.Mills,
                    EventType = "Carb Correction",
                    Carbs = c.Carbs,
                });
            }
            recentTreatments = recentTreatments.OrderByDescending(t => t.Mills).Take(10).ToList();
            var response = new RetrospectiveDataResponse
            {
                Time = time,
                TimeFormatted = DateTimeOffset.FromUnixTimeMilliseconds(time).ToString("HH:mm:ss"),
                Glucose = glucoseData,
                Iob = new IobData
                {
                    Total = iobResult.Iob,
                    Bolus = iobResult.Iob - (iobResult.BasalIob ?? 0),
                    Basal = iobResult.BasalIob ?? 0,
                    Activity = iobResult.Activity,
                    Source = iobResult.Source
                },
                Cob = new CobData
                {
                    Total = cobResult.Cob,
                    IsDecaying = cobResult.IsDecaying ?? 0,
                    CarbsHr = cobResult.CarbsHr,
                    RawCarbImpact = cobResult.RawCarbImpact,
                    Source = cobResult.Source
                },
                Basal = basalData,
                RecentTreatments = recentTreatments
            };
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating retrospective data for time {Time}", time);
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }
    /// <summary>
    /// Get retrospective data for an entire day at specified interval
    /// Returns IOB, COB, glucose, and basal data for every interval throughout the day
    /// </summary>
    /// <param name="date">Date in YYYY-MM-DD format</param>
    /// <param name="intervalMinutes">Interval in minutes between data points (default: 5)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of retrospective data points for the entire day</returns>
    /// <response code="200">Returns the retrospective timeline data</response>
    /// <response code="400">If parameters are invalid</response>
    /// <response code="500">If there was an internal server error</response>
    [HttpGet("timeline")]
    [RemoteQuery]
    [ProducesResponseType(typeof(RetrospectiveTimelineResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<RetrospectiveTimelineResponse>> GetRetrospectiveTimeline(
        [FromQuery] string date,
        [FromQuery] int intervalMinutes = 5,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrEmpty(date) || !DateTimeOffset.TryParse(date, out var parsedDate))
            {
                return Problem(detail: "Date parameter must be in YYYY-MM-DD format", statusCode: 400, title: "Bad Request");
            }
            if (intervalMinutes < 1 || intervalMinutes > 60)
            {
                return Problem(detail: "Interval must be between 1 and 60 minutes", statusCode: 400, title: "Bad Request");
            }
            // Calculate day boundaries
            var dayStart = new DateTimeOffset(parsedDate.Year, parsedDate.Month, parsedDate.Day, 0, 0, 0, TimeSpan.Zero);
            var dayEnd = dayStart.AddDays(1).AddMilliseconds(-1);
            var startMills = dayStart.ToUnixTimeMilliseconds();
            var endMills = dayEnd.ToUnixTimeMilliseconds();
            // Fetch all data for the day (plus 8 hours before for IOB calculation context)
            var fetchFrom = dayStart.UtcDateTime.AddHours(-8);
            var fetchTo = dayEnd.UtcDateTime;
            // Get entries for the day
            var entries = await _entryService.GetEntriesAsync(
                $"find[date][$gte]={startMills}&find[date][$lte]={endMills}",
                count: 5000,
                skip: 0,
                cancellationToken
            );
            var entryList = entries?.ToList() ?? new List<Entry>();
            // Query v4 types
            var boluses = (await _bolusRepository.GetAsync(
                from: fetchFrom, to: fetchTo, device: null, source: null,
                limit: 5000, offset: 0, descending: false, ct: cancellationToken
            )).ToList();
            var tempBasals = (await _tempBasalRepository.GetAsync(
                from: fetchFrom, to: fetchTo, device: null, source: null,
                limit: 5000, offset: 0, descending: false, ct: cancellationToken
            )).ToList();
            var carbIntakes = (await _carbIntakeRepository.GetAsync(
                from: fetchFrom, to: fetchTo, device: null, source: null,
                limit: 5000, offset: 0, descending: false, ct: cancellationToken
            )).ToList();
            // Get device status
            var deviceStatus = await _projectionService.GetAsync(
                count: 500,
                skip: 0,
                find: null,
                ct: cancellationToken
            );
            var deviceStatusList = deviceStatus?
                .Where(d => d.Mills >= startMills && d.Mills <= endMills)
                .ToList() ?? new List<DeviceStatus>();
            // Calculate data points at each interval
            var dataPoints = new List<RetrospectiveDataPoint>();
            var totalIntervals = (24 * 60) / intervalMinutes;
            for (int i = 0; i < totalIntervals; i++)
            {
                var pointTime = startMills + (i * intervalMinutes * 60 * 1000);
                var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(pointTime);
                // Filter records relevant to this time point
                var relevantBoluses = boluses.Where(b => b.Mills <= pointTime).ToList();
                var relevantCarbIntakes = carbIntakes.Where(c => c.Mills <= pointTime).ToList();
                var relevantTempBasals = tempBasals.Where(t => t.StartMills <= pointTime).ToList();
                // Calculate IOB
                var iobResult = _iobCalculator.FromBoluses(relevantBoluses, pointTime);
                // Calculate COB
                var cobResult = _cobCalculator.FromCarbIntakes(
                    relevantCarbIntakes,
                    relevantBoluses,
                    relevantTempBasals,
                    pointTime
                );
                // Get glucose at this time
                var glucose = GetGlucoseAtTime(entryList, pointTime);
                // Get basal rate
                var basal = await GetBasalRateAtTimeAsync(tempBasals, pointTime);
                dataPoints.Add(new RetrospectiveDataPoint
                {
                    Time = pointTime,
                    Hour = timestamp.Hour,
                    Minute = timestamp.Minute,
                    TimeLabel = timestamp.ToString("HH:mm"),
                    Glucose = glucose?.Value,
                    GlucoseDirection = glucose?.Direction,
                    Iob = Math.Round(iobResult.Iob, 3),
                    BolusIob = Math.Round(iobResult.Iob - (iobResult.BasalIob ?? 0), 3),
                    BasalIob = Math.Round(iobResult.BasalIob ?? 0, 3),
                    Cob = Math.Round(cobResult.Cob, 1),
                    BasalRate = basal?.Rate ?? 0,
                    IsTemp = basal?.IsTemp ?? false
                });
            }
            var response = new RetrospectiveTimelineResponse
            {
                Date = date,
                StartTime = startMills,
                EndTime = endMills,
                IntervalMinutes = intervalMinutes,
                TotalPoints = dataPoints.Count,
                Data = dataPoints
            };
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating retrospective timeline for date {Date}", date);
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }
    /// <summary>
    /// Get basal rate timeline for a day
    /// Returns basal rate data points showing scheduled and temp basal changes
    /// </summary>
    /// <param name="date">Date in YYYY-MM-DD format</param>
    /// <param name="intervalMinutes">Interval in minutes between data points (default: 5)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Basal rate timeline for the day</returns>
    [HttpGet("basal-timeline")]
    [RemoteQuery]
    [ProducesResponseType(typeof(BasalTimelineResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<BasalTimelineResponse>> GetBasalTimeline(
        [FromQuery] string date,
        [FromQuery] int intervalMinutes = 5,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrEmpty(date) || !DateTimeOffset.TryParse(date, out var parsedDate))
            {
                return Problem(detail: "Date parameter must be in YYYY-MM-DD format", statusCode: 400, title: "Bad Request");
            }
            // Calculate day boundaries
            var dayStart = new DateTimeOffset(parsedDate.Year, parsedDate.Month, parsedDate.Day, 0, 0, 0, TimeSpan.Zero);
            var dayEnd = dayStart.AddDays(1);
            var startMills = dayStart.ToUnixTimeMilliseconds();
            var endMills = dayEnd.ToUnixTimeMilliseconds();
            // Query temp basals (include 1 hour before for active temp basals spanning midnight)
            var fetchFrom = dayStart.UtcDateTime.AddHours(-1);
            var fetchTo = dayEnd.UtcDateTime;
            var tempBasals = (await _tempBasalRepository.GetAsync(
                from: fetchFrom, to: fetchTo, device: null, source: null,
                limit: 2000, offset: 0, descending: false, ct: cancellationToken
            )).ToList();
            // Generate basal timeline
            var dataPoints = new List<BasalDataPoint>();
            var totalIntervals = (24 * 60) / intervalMinutes;
            for (int i = 0; i < totalIntervals; i++)
            {
                var pointTime = startMills + (i * intervalMinutes * 60 * 1000);
                var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(pointTime);
                var basal = await GetBasalRateAtTimeAsync(tempBasals, pointTime);
                dataPoints.Add(new BasalDataPoint
                {
                    Time = pointTime,
                    Hour = timestamp.Hour,
                    Minute = timestamp.Minute,
                    TimeLabel = timestamp.ToString("HH:mm"),
                    Rate = basal?.Rate ?? await GetScheduledBasalRateAsync(pointTime),
                    IsTemp = basal?.IsTemp ?? false
                });
            }
            var response = new BasalTimelineResponse
            {
                Date = date,
                StartTime = startMills,
                EndTime = endMills,
                IntervalMinutes = intervalMinutes,
                Data = dataPoints
            };
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating basal timeline for date {Date}", date);
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }
    #region Helper Methods
    /// <summary>
    /// Get interpolated glucose value at a specific time
    /// </summary>
    private GlucoseData? GetGlucoseAtTime(List<Entry> entries, long targetTime)
    {
        if (entries == null || entries.Count == 0) return null;
        // Sort entries by time
        var sortedEntries = entries
            .Where(e => e.Mills > 0 && (e.Sgv ?? e.Mgdl) > 0)
            .OrderBy(e => e.Mills)
            .ToList();
        if (sortedEntries.Count == 0) return null;
        // Find entries before and after target time
        Entry? before = null;
        Entry? after = null;
        foreach (var entry in sortedEntries)
        {
            if (entry.Mills <= targetTime)
            {
                before = entry;
            }
            else if (after == null && entry.Mills > targetTime)
            {
                after = entry;
                break;
            }
        }
        // If exact match or very close (within 2.5 minutes)
        if (before != null && Math.Abs(before.Mills - targetTime) < 2.5 * 60 * 1000)
        {
            return new GlucoseData
            {
                Value = (int)(before.Sgv ?? before.Mgdl),
                Direction = before.Direction,
                Delta = CalculateDelta(sortedEntries, before)
            };
        }
        // Interpolate between before and after
        if (before != null && after != null)
        {
            var beforeValue = (int)(before.Sgv ?? before.Mgdl);
            var afterValue = (int)(after.Sgv ?? after.Mgdl);
            var ratio = (double)(targetTime - before.Mills) / (after.Mills - before.Mills);
            var interpolated = beforeValue + (afterValue - beforeValue) * ratio;
            return new GlucoseData
            {
                Value = (int)Math.Round((double)interpolated),
                Direction = ratio < 0.5 ? before.Direction : after.Direction,
                Delta = afterValue - beforeValue
            };
        }
        // Only have data before or after
        if (before != null)
        {
            return new GlucoseData
            {
                Value = (int)(before.Sgv ?? before.Mgdl),
                Direction = before.Direction,
                Delta = CalculateDelta(sortedEntries, before)
            };
        }
        if (after != null)
        {
            return new GlucoseData
            {
                Value = (int)(after.Sgv ?? after.Mgdl),
                Direction = after.Direction,
                Delta = null
            };
        }
        return null;
    }
    /// <summary>
    /// Calculate delta from previous entry
    /// </summary>
    private int? CalculateDelta(List<Entry> entries, Entry current)
    {
        var targetPrevTime = current.Mills - 5 * 60 * 1000; // 5 minutes ago
        var previous = entries
            .Where(e => e.Mills < current.Mills && Math.Abs(e.Mills - targetPrevTime) < 10 * 60 * 1000)
            .OrderByDescending(e => e.Mills)
            .FirstOrDefault();
        if (previous != null)
        {
            var currentValue = (int)(current.Sgv ?? current.Mgdl);
            var previousValue = (int)(previous.Sgv ?? previous.Mgdl);
            return currentValue - previousValue;
        }
        return null;
    }
    /// <summary>
    /// Get basal rate at a specific time (checking for active V4 temp basals)
    /// </summary>
    private async Task<BasalData?> GetBasalRateAtTimeAsync(List<TempBasal> tempBasals, long targetTime)
    {
        // Look for active temp basal at target time
        foreach (var tb in tempBasals)
        {
            var startTime = tb.StartMills;
            var endTime = tb.EndMills;
            if (endTime == null) continue;
            if (targetTime >= startTime && targetTime < endTime)
            {
                return new BasalData
                {
                    Rate = tb.Rate,
                    IsTemp = true
                };
            }
        }
        // Return scheduled basal rate
        return new BasalData
        {
            Rate = await GetScheduledBasalRateAsync(targetTime),
            IsTemp = false
        };
    }
    /// <summary>
    /// Get scheduled basal rate from profile
    /// </summary>
    private async Task<double> GetScheduledBasalRateAsync(long time)
    {
        try
        {
            return await _basalRateResolver.GetBasalRateAsync(time);
        }
        catch
        {
            return 0.8; // Default basal rate if profile not available
        }
    }
    #endregion
}
#region Response Models
/// <summary>
/// Glucose data at a specific point in time
/// </summary>
public class GlucoseData
{
    public int Value { get; set; }
    public string? Direction { get; set; }
    public int? Delta { get; set; }
}
/// <summary>
/// IOB data at a specific point in time
/// </summary>
public class IobData
{
    public double Total { get; set; }
    public double Bolus { get; set; }
    public double Basal { get; set; }
    public double? Activity { get; set; }
    public string? Source { get; set; }
}
/// <summary>
/// COB data at a specific point in time
/// </summary>
public class CobData
{
    public double Total { get; set; }
    public double IsDecaying { get; set; }
    public double? CarbsHr { get; set; }
    public double? RawCarbImpact { get; set; }
    public string? Source { get; set; }
}
/// <summary>
/// Basal rate data at a specific point in time
/// </summary>
public class BasalData
{
    public double Rate { get; set; }
    public bool IsTemp { get; set; }
}
/// <summary>
/// Treatment summary for recent treatments
/// </summary>
public class TreatmentSummaryData
{
    public string? Id { get; set; }
    public long Mills { get; set; }
    public string? EventType { get; set; }
    public double? Insulin { get; set; }
    public double? Carbs { get; set; }
    public double? Rate { get; set; }
    public double? Duration { get; set; }
    public string? Notes { get; set; }
}
/// <summary>
/// Response for single point retrospective data
/// </summary>
public class RetrospectiveDataResponse
{
    public long Time { get; set; }
    public string? TimeFormatted { get; set; }
    public GlucoseData? Glucose { get; set; }
    public IobData? Iob { get; set; }
    public CobData? Cob { get; set; }
    public BasalData? Basal { get; set; }
    public List<TreatmentSummaryData>? RecentTreatments { get; set; }
}
/// <summary>
/// Data point for retrospective timeline
/// </summary>
public class RetrospectiveDataPoint
{
    public long Time { get; set; }
    public int Hour { get; set; }
    public int Minute { get; set; }
    public string? TimeLabel { get; set; }
    public int? Glucose { get; set; }
    public string? GlucoseDirection { get; set; }
    public double Iob { get; set; }
    public double BolusIob { get; set; }
    public double BasalIob { get; set; }
    public double Cob { get; set; }
    public double BasalRate { get; set; }
    public bool IsTemp { get; set; }
}
/// <summary>
/// Response for retrospective timeline
/// </summary>
public class RetrospectiveTimelineResponse
{
    public string? Date { get; set; }
    public long StartTime { get; set; }
    public long EndTime { get; set; }
    public int IntervalMinutes { get; set; }
    public int TotalPoints { get; set; }
    public List<RetrospectiveDataPoint>? Data { get; set; }
}
/// <summary>
/// Data point for basal timeline
/// </summary>
public class BasalDataPoint
{
    public long Time { get; set; }
    public int Hour { get; set; }
    public int Minute { get; set; }
    public string? TimeLabel { get; set; }
    public double Rate { get; set; }
    public bool IsTemp { get; set; }
}
/// <summary>
/// Response for basal timeline
/// </summary>
public class BasalTimelineResponse
{
    public string? Date { get; set; }
    public long StartTime { get; set; }
    public long EndTime { get; set; }
    public int IntervalMinutes { get; set; }
    public List<BasalDataPoint>? Data { get; set; }
}
#endregion
