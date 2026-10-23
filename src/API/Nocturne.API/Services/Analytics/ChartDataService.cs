using System.Text.Json;
using Nocturne.API.Helpers;
using Nocturne.API.Services.ChartData;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Analytics;

/// <summary>
/// Service that orchestrates all data fetching and computation for the dashboard chart.
/// Executes a sequential pipeline of <see cref="IChartDataStage"/> instances, each populating
/// the shared <see cref="ChartDataContext"/>, then assembles the final <see cref="DashboardChartData"/>
/// DTO via <see cref="IChartDataAssembler"/>.
/// </summary>
/// <remarks>
/// A fixed 8-hour buffer is prepended to <see cref="ChartDataContext.StartTime"/> so that IOB/COB
/// calculations have enough treatment history to be accurate at the start of the requested window.
/// </remarks>
/// <seealso cref="IChartDataService"/>
/// <seealso cref="IChartDataStage"/>
/// <seealso cref="IChartDataAssembler"/>
/// <seealso cref="ChartDataContext"/>
public class ChartDataService : IChartDataService
{
    internal const int TempBasalQueryLimit = 131072;
    internal const long BufferMs = 8L * 60 * 60 * 1000;

    private readonly IEnumerable<IChartDataStage> _pipeline;
    private readonly IChartDataAssembler _assembler;
    private readonly IBasalSeriesBuilder _basalSeriesBuilder;
    private readonly ITempBasalRepository _tempBasalRepository;
    private readonly ITherapySettingsResolver _therapySettingsResolver;
    private readonly IBasalRateResolver _basalRateResolver;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChartDataService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ChartDataService"/>.
    /// </summary>
    /// <param name="pipeline">The ordered sequence of <see cref="IChartDataStage"/> stages to execute.</param>
    /// <param name="assembler">The assembler that converts the completed context into the final DTO.</param>
    /// <param name="basalSeriesBuilder">Builds basal delivery series from temp basals and profile rates.</param>
    /// <param name="tempBasalRepository">Repository for fetching temp basal records.</param>
    /// <param name="therapySettingsResolver">Resolves whether therapy settings (profile) data exists.</param>
    /// <param name="basalRateResolver">Resolves the active basal rate at a point in time.</param>
    /// <param name="timeProvider">The clock a temp basal still running stops at.</param>
    /// <param name="logger">The logger instance.</param>
    public ChartDataService(
        IEnumerable<IChartDataStage> pipeline,
        IChartDataAssembler assembler,
        IBasalSeriesBuilder basalSeriesBuilder,
        ITempBasalRepository tempBasalRepository,
        ITherapySettingsResolver therapySettingsResolver,
        IBasalRateResolver basalRateResolver,
        TimeProvider timeProvider,
        ILogger<ChartDataService> logger
    )
    {
        _pipeline = pipeline;
        _assembler = assembler;
        _basalSeriesBuilder = basalSeriesBuilder;
        _tempBasalRepository = tempBasalRepository;
        _therapySettingsResolver = therapySettingsResolver;
        _basalRateResolver = basalRateResolver;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DashboardChartData> GetDashboardChartDataAsync(
        long startTime,
        long endTime,
        int intervalMinutes,
        bool includeHealthSeries,
        CancellationToken cancellationToken = default
    )
    {
        var context = new ChartDataContext
        {
            StartTime = startTime,
            EndTime = endTime,
            IntervalMinutes = intervalMinutes,
            BufferStartTime = startTime - BufferMs,
            IncludeHealthSeries = includeHealthSeries,
        };

        foreach (var stage in _pipeline)
        {
            context = await stage.ExecuteAsync(context, cancellationToken);
        }

        return _assembler.Assemble(context);
    }

    /// <inheritdoc />
    public async Task<List<BasalPoint>> GetBasalSeriesAsync(
        long startTime,
        long endTime,
        CancellationToken cancellationToken = default
    )
    {
        var defaultBasalRate = 1.0;
        var hasData = await _therapySettingsResolver.HasDataAsync(cancellationToken);
        if (hasData)
            defaultBasalRate = await _basalRateResolver.GetBasalRateAsync(endTime, ct: cancellationToken);

        var tempBasals = (await _tempBasalRepository.GetAsync(
            from: DateTimeOffset.FromUnixTimeMilliseconds(startTime - BufferMs).UtcDateTime,
            to: DateTimeOffset.FromUnixTimeMilliseconds(endTime).UtcDateTime,
            device: null,
            source: null,
            limit: TempBasalQueryLimit,
            offset: 0,
            descending: false,
            ct: cancellationToken
        )).ToList();
        return await _basalSeriesBuilder.BuildAsync(
            SelectDisplayTempBasals(tempBasals, startTime, endTime, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()),
            startTime,
            endTime,
            defaultBasalRate,
            cancellationToken
        );
    }

    #region Internal Helpers

    /// <summary>
    /// The delivered pieces (<see cref="StatisticsService.ClipOverlappingTempBasals"/>) that overlap
    /// the window. <paramref name="tempBasals"/> must reach back before the window, so a record
    /// cancelled before it is cut by the record that cancelled it. A temp still running counts to
    /// <paramref name="nowMills"/>, or to <paramref name="endTime"/> when that comes first: the
    /// live chart asks for a window that ends ahead of now.
    /// </summary>
    internal static List<TempBasal> SelectDisplayTempBasals(
        IEnumerable<TempBasal> tempBasals,
        long startTime,
        long endTime,
        long nowMills
    ) =>
        StatisticsService.ClipOverlappingTempBasals(tempBasals, Math.Min(endTime, nowMills))
            .Where(tb => tb.StartMills <= endTime && tb.EndMills > startTime)
            .ToList();

    internal static (List<GlucosePointDto> data, double yMax) BuildGlucoseData(
        List<SensorGlucose> readings
    )
    {
        var sorted = readings.OrderBy(r => r.Mills).ToList();

        var glucoseData = sorted
            .Select(r => new GlucosePointDto
            {
                Time = r.Mills,
                Sgv = r.Mgdl,
                Direction = r.Direction?.ToWireString(),
                DataSource = r.DataSource ?? r.Device,
            })
            .ToList(); // Already sorted

        var maxSgv = glucoseData.Any() ? glucoseData.Max(g => g.Sgv) : 280;
        var glucoseYMax = Math.Min(ChartThresholdsBuilder.GlucoseYMaxCap, Math.Max(280, maxSgv) + 20);

        return (glucoseData, glucoseYMax);
    }

    internal static List<BolusMarkerDto> BuildBolusMarkers(List<Bolus> boluses)
    {
        var sorted = boluses.Where(b => b.Insulin > 0).OrderBy(b => b.Mills).ToList();

        return sorted
            .Select(b => new BolusMarkerDto
            {
                Time = b.Mills,
                Insulin = b.Insulin,
                TreatmentId = b.LegacyId ?? b.Id.ToString(),
                BolusType = MapV4BolusType(b.BolusType, b.Automatic),
                IsOverride = false,
                DataSource = b.DataSource ?? b.Device,
            })
            .ToList();
    }

    internal static List<CarbMarkerDto> BuildCarbMarkers(
        List<CarbIntake> carbIntakes,
        string? timezone
    )
    {
        var sorted = carbIntakes.Where(c => c.Carbs > 0).OrderBy(c => c.Mills).ToList();

        return sorted
            .Select(c => new CarbMarkerDto
            {
                Time = c.Mills,
                Carbs = c.Carbs,
                Label = GetMealNameForTime(c.Mills, timezone),
                TreatmentId = c.LegacyId ?? c.Id.ToString(),
                IsOffset = false,
                DataSource = c.DataSource ?? c.Device,
            })
            .ToList();
    }

    internal static List<DeviceEventMarkerDto> BuildDeviceEventMarkers(
        List<DeviceEvent> deviceEvents
    )
    {
        var sorted = deviceEvents.OrderBy(e => e.Mills).ToList();

        return sorted
            .Select(e => new DeviceEventMarkerDto
            {
                Time = e.Mills,
                EventType = e.EventType,
                Notes = e.Notes,
                TreatmentId = e.LegacyId ?? e.Id.ToString(),
                Color = ChartColorMapper.FromDeviceEvent(e.EventType),
                IsInferred = e.IsInferred,
            })
            .ToList();
    }

    internal static List<BgCheckMarkerDto> BuildBgCheckMarkers(List<BGCheck> bgChecks)
    {
        var sorted = bgChecks.Where(b => b.Mgdl > 0).OrderBy(b => b.Mills).ToList();

        return sorted
            .Select(b => new BgCheckMarkerDto
            {
                Time = b.Mills,
                Glucose = b.Mgdl,
                GlucoseType = b.GlucoseType?.ToString(),
                TreatmentId = b.LegacyId ?? b.Id.ToString(),
            })
            .ToList();
    }

    /// <summary>
    /// Maps v4 BolusType enum to the chart BolusType enum.
    /// The v4 model uses a simpler BolusType (Normal, Square, Dual) plus an Automatic flag,
    /// while the chart uses a more granular BolusType derived from legacy event type strings.
    /// </summary>
    internal static Nocturne.Core.Models.BolusType MapV4BolusType(
        Nocturne.Core.Models.V4.BolusType? v4Type,
        bool automatic
    )
    {
        if (automatic)
            return Nocturne.Core.Models.BolusType.AutomaticBolus;

        return v4Type switch
        {
            Nocturne.Core.Models.V4.BolusType.Square => Nocturne.Core.Models.BolusType.ComboBolus,
            Nocturne.Core.Models.V4.BolusType.Dual => Nocturne.Core.Models.BolusType.ComboBolus,
            _ => Nocturne.Core.Models.BolusType.Bolus,
        };
    }

    internal static List<BasalDeliverySpanDto> MapBasalDeliverySpans(
        List<TempBasal> tempBasals
    )
    {
        return WithPieceIds(tempBasals.Where(tb => tb.Origin == TempBasalOrigin.Suspended || tb.Rate is not null))
            .Select(piece =>
            {
                var (tb, id) = piece;
                var origin = BasalSeriesBuilder.MapTempBasalOrigin(tb.Origin);
                return new BasalDeliverySpanDto
                {
                    Id = id,
                    StartMills = tb.StartMills,
                    EndMills = tb.EndMills,
                    Rate = origin == BasalDeliveryOrigin.Suspended ? 0 : tb.Rate!.Value,
                    Origin = origin,
                    Source = tb.DataSource,
                    FillColor = ChartColorMapper.FillFromBasalOrigin(origin),
                    StrokeColor = ChartColorMapper.StrokeFromBasalOrigin(origin),
                };
            })
            .ToList();
    }

    /// <summary>
    /// The later of the newest pump status's pump clock (its upload time when it reports none) and
    /// the end of the newest temp basal, or the start of an open one. A pump running its scheduled
    /// basal has no temp basal to show for it, which AAPS leaves for long stretches while its
    /// devicestatus keeps reporting the pump every loop, so the temp basals alone date the pump's
    /// last contact to whenever the last one ended.
    /// </summary>
    internal static long? LastPumpSyncMills(IEnumerable<TempBasal> tempBasals, PumpSnapshot? latestPump)
    {
        long? latest = null;
        foreach (var tb in tempBasals)
        {
            var end = tb.EndMills ?? tb.StartMills;
            if (latest is null || end > latest)
                latest = end;
        }

        if (latestPump is not null)
        {
            var contact = UploaderTimestamp.ParseUtcDateTime(latestPump.Clock) is { } clock
                ? new DateTimeOffset(DateTime.SpecifyKind(clock, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
                : latestPump.Mills;
            if (latest is null || contact > latest)
                latest = contact;
        }

        return latest;
    }

    internal static List<ChartStateSpanDto> MapTempBasalSpans(
        List<TempBasal> tempBasals
    )
    {
        return WithPieceIds(tempBasals.Where(tb => tb.Origin == TempBasalOrigin.Manual))
            .Select(piece => new ChartStateSpanDto
            {
                Id = piece.Id,
                Category = StateSpanCategory.PumpMode, // Rendered in dedicated tempBasalSpans list; category is informational
                State = "TempBasal",
                StartMills = piece.Tb.StartMills,
                EndMills = piece.Tb.EndMills,
                Color = ChartColor.InsulinTempBasal,
                Metadata = null,
            })
            .ToList();
    }

    /// <summary>
    /// Each delivered piece with its span id: the piece that starts its record gets the record's id,
    /// and any later piece of a record that a winning stream interrupts gets that id with the piece's
    /// start. The id depends only on the piece, not on the window it is served in, since the chart
    /// joins responses by id.
    /// </summary>
    private static IEnumerable<(TempBasal Tb, string Id)> WithPieceIds(IEnumerable<TempBasal> pieces)
    {
        foreach (var tb in pieces)
        {
            var id = tb.LegacyId ?? tb.Id.ToString();
            yield return (tb, tb.StartMills == tb.RecordStartMills ? id : $"{id}:{tb.StartMills}");
        }
    }

    internal static List<SystemEventMarkerDto> MapSystemEvents(
        IEnumerable<SystemEvent>? systemEvents
    )
    {
        return (systemEvents ?? Enumerable.Empty<SystemEvent>())
            .Select(e => new SystemEventMarkerDto
            {
                Id = e.Id ?? "",
                Time = e.Mills,
                EventType = e.EventType,
                Category = e.Category,
                Code = e.Code,
                Description = e.Description,
                Color = ChartColorMapper.FromSystemEvent(e.EventType),
            })
            .ToList();
    }

    internal static List<TrackerMarkerDto> MapTrackerMarkers(
        IEnumerable<TrackerDefinitionEntity> trackerDefs,
        IEnumerable<TrackerInstanceEntity> trackerInstances,
        long startTime,
        long endTime
    )
    {
        var defsList = trackerDefs.ToList();
        return trackerInstances
            .Where(i => i.ExpectedEndAt.HasValue)
            .Where(i =>
            {
                var expectedMills = new DateTimeOffset(
                    i.ExpectedEndAt!.Value,
                    TimeSpan.Zero
                ).ToUnixTimeMilliseconds();
                return expectedMills >= startTime && expectedMills <= endTime;
            })
            .Select(i =>
            {
                var def = defsList.FirstOrDefault(d => d.Id == i.DefinitionId);
                var category = def?.Category ?? TrackerCategory.Custom;
                var expectedMills = new DateTimeOffset(
                    i.ExpectedEndAt!.Value,
                    TimeSpan.Zero
                ).ToUnixTimeMilliseconds();

                return new TrackerMarkerDto
                {
                    Id = i.Id.ToString(),
                    DefinitionId = i.DefinitionId.ToString(),
                    Name = def?.Name ?? "Tracker",
                    Category = category,
                    Time = expectedMills,
                    Icon = def?.Icon,
                    Color = ChartColorMapper.FromTracker(category),
                };
            })
            .OrderBy(m => m.Time)
            .ToList();
    }

    internal static (double rate, BasalDeliveryOrigin origin) ExtractBasalDeliveryMetadata(
        StateSpan span,
        double defaultRate
    )
    {
        double rate = defaultRate;
        if (span.Metadata?.TryGetValue("rate", out var rateObj) == true)
        {
            rate = rateObj switch
            {
                JsonElement jsonElement => jsonElement.GetDouble(),
                double d => d,
                _ => Convert.ToDouble(rateObj),
            };
        }

        string? originStr = "Scheduled";
        if (span.Metadata?.TryGetValue("origin", out var originObj) == true)
        {
            originStr = originObj switch
            {
                JsonElement jsonElement => jsonElement.GetString(),
                string s => s,
                _ => originObj?.ToString(),
            };
        }

        var origin = originStr?.ToLowerInvariant() switch
        {
            "algorithm" => BasalDeliveryOrigin.Algorithm,
            "manual" => BasalDeliveryOrigin.Manual,
            "suspended" => BasalDeliveryOrigin.Suspended,
            _ => BasalDeliveryOrigin.Scheduled,
        };

        return (rate, origin);
    }

    internal static string GetMealNameForTime(long mills, string? timezone)
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds(mills);
        if (!string.IsNullOrEmpty(timezone))
            time = TimeZoneInfo.ConvertTime(time, TimeZoneHelper.GetTimeZoneInfoFromId(timezone));
        return time.Hour switch
        {
            >= 5 and < 11 => "Breakfast",
            >= 11 and < 15 => "Lunch",
            >= 15 and < 17 => "Snack",
            >= 17 and < 21 => "Dinner",
            _ => "Late Night",
        };
    }

    #endregion
}
