using System.Text.Json;
using Nocturne.API.Helpers;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services;

/// <summary>
/// Service that orchestrates all data fetching and computation for the dashboard chart.
/// Loads profiles, fetches entries/treatments/state spans, builds IOB/COB/basal series,
/// categorizes treatments, maps spans, and assembles the final DTO.
/// </summary>
public class ChartDataService : IChartDataService
{
    private readonly IIobService _iobService;
    private readonly ICobService _cobService;
    private readonly ITreatmentService _treatmentService;
    private readonly ITreatmentFoodService _treatmentFoodService;
    private readonly IEntryService _entryService;
    private readonly IDeviceStatusService _deviceStatusService;
    private readonly IProfileService _profileService;
    private readonly IProfileDataService _profileDataService;
    private readonly StateSpanRepository _stateSpanRepository;
    private readonly SystemEventRepository _systemEventRepository;
    private readonly TrackerRepository _trackerRepository;
    private readonly ILogger<ChartDataService> _logger;

    // Clinical standard thresholds (mg/dL) -- used when profile doesn't specify
    private const double DefaultVeryLow = 54;
    private const double DefaultVeryHigh = 250;

    public ChartDataService(
        IIobService iobService,
        ICobService cobService,
        ITreatmentService treatmentService,
        ITreatmentFoodService treatmentFoodService,
        IEntryService entryService,
        IDeviceStatusService deviceStatusService,
        IProfileService profileService,
        IProfileDataService profileDataService,
        StateSpanRepository stateSpanRepository,
        SystemEventRepository systemEventRepository,
        TrackerRepository trackerRepository,
        ILogger<ChartDataService> logger
    )
    {
        _iobService = iobService;
        _cobService = cobService;
        _treatmentService = treatmentService;
        _treatmentFoodService = treatmentFoodService;
        _entryService = entryService;
        _deviceStatusService = deviceStatusService;
        _profileService = profileService;
        _profileDataService = profileDataService;
        _stateSpanRepository = stateSpanRepository;
        _systemEventRepository = systemEventRepository;
        _trackerRepository = trackerRepository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DashboardChartData> GetDashboardChartDataAsync(
        long startTime,
        long endTime,
        int intervalMinutes,
        CancellationToken cancellationToken = default
    )
    {
        // Load profile data
        var profiles = await _profileDataService.GetProfilesAsync(
            count: 100,
            cancellationToken: cancellationToken
        );
        var profileList = profiles?.ToList() ?? new List<Profile>();
        if (profileList.Any())
        {
            _profileService.LoadData(profileList);
            _logger.LogDebug("Loaded {Count} profiles into profile service", profileList.Count);
        }

        // Get profile-based configuration
        var timezone = _profileService.HasData() ? _profileService.GetTimezone() : null;
        var thresholds = GetProfileThresholds(endTime);

        // Fetch all data sequentially (DbContext is not thread-safe)
        var bufferMs = 8L * 60 * 60 * 1000; // 8 hours buffer for IOB calculation

        // Time-range filtered queries
        var treatmentFind = $"{{\"mills\":{{\"$gte\":{startTime - bufferMs},\"$lte\":{endTime}}}}}";
        var relevantTreatments =
            (
                await _treatmentService.GetTreatmentsAsync(
                    find: treatmentFind,
                    count: int.MaxValue,
                    cancellationToken: cancellationToken
                )
            )?.ToList() ?? new List<Treatment>();

        var entryFind = $"{{\"mills\":{{\"$gte\":{startTime},\"$lte\":{endTime}}}}}";
        var relevantEntries =
            (
                await _entryService.GetEntriesAsync(
                    find: entryFind,
                    count: int.MaxValue,
                    cancellationToken: cancellationToken
                )
            )?.ToList() ?? new List<Entry>();

        var deviceStatusList =
            (
                await _deviceStatusService.GetDeviceStatusAsync(
                    count: int.MaxValue,
                    skip: 0,
                    cancellationToken: cancellationToken
                )
            )?.ToList() ?? new List<DeviceStatus>();

        var displayTreatments = relevantTreatments
            .Where(t => t.Mills >= startTime && t.Mills <= endTime)
            .ToList();

        // Fetch state spans
        var pumpModeSpansResult = await _stateSpanRepository.GetByCategory(
            StateSpanCategory.PumpMode,
            startTime,
            endTime,
            cancellationToken
        );
        var basalDeliverySpansResult = await _stateSpanRepository.GetByCategory(
            StateSpanCategory.BasalDelivery,
            startTime,
            endTime,
            cancellationToken
        );
        var profileSpansResult = await _stateSpanRepository.GetByCategory(
            StateSpanCategory.Profile,
            startTime,
            endTime,
            cancellationToken
        );
        var overrideSpansResult = await _stateSpanRepository.GetByCategory(
            StateSpanCategory.Override,
            startTime,
            endTime,
            cancellationToken
        );
        var sleepSpansResult = await _stateSpanRepository.GetByCategory(
            StateSpanCategory.Sleep,
            startTime,
            endTime,
            cancellationToken
        );
        var exerciseSpansResult = await _stateSpanRepository.GetByCategory(
            StateSpanCategory.Exercise,
            startTime,
            endTime,
            cancellationToken
        );
        var illnessSpansResult = await _stateSpanRepository.GetByCategory(
            StateSpanCategory.Illness,
            startTime,
            endTime,
            cancellationToken
        );
        var travelSpansResult = await _stateSpanRepository.GetByCategory(
            StateSpanCategory.Travel,
            startTime,
            endTime,
            cancellationToken
        );

        // System events
        var systemEventsResult = await _systemEventRepository.GetSystemEventsAsync(
            eventType: null,
            category: null,
            from: startTime,
            to: endTime,
            source: null,
            count: 500,
            skip: 0,
            cancellationToken: cancellationToken
        );

        // Tracker data
        var trackerDefs = await _trackerRepository.GetAllDefinitionsAsync(cancellationToken);
        var trackerInstances = await _trackerRepository.GetActiveInstancesAsync(
            userId: null,
            cancellationToken: cancellationToken
        );

        // Get default basal rate
        var defaultBasalRate = _profileService.HasData()
            ? _profileService.GetBasalRate(endTime, null)
            : 1.0;

        // Build computed series
        var (iobSeries, cobSeries, maxIob, maxCob) = BuildIobCobSeries(
            relevantTreatments,
            deviceStatusList,
            startTime,
            endTime,
            intervalMinutes
        );

        var basalDeliverySpans = basalDeliverySpansResult.ToList();
        var basalSeries = BuildBasalSeriesFromStateSpans(
            basalDeliverySpans,
            startTime,
            endTime,
            defaultBasalRate
        );
        var maxBasalRate = Math.Max(
            defaultBasalRate * 2.5,
            basalSeries.Any() ? basalSeries.Max(b => b.Rate) : defaultBasalRate
        );

        var (glucoseData, glucoseYMax) = BuildGlucoseData(relevantEntries);

        // Categorize treatments
        var (bolusMarkers, carbMarkers, deviceEventMarkers, carbTreatmentIds) =
            CategorizeTreatments(displayTreatments, timezone);

        // Process food offsets
        await ProcessFoodOffsetsAsync(
            carbMarkers,
            carbTreatmentIds,
            displayTreatments,
            cancellationToken
        );

        // Map state spans
        var pumpModeSpanDtos = MapStateSpans(pumpModeSpansResult, StateSpanCategory.PumpMode);
        var profileSpanDtos = MapStateSpans(profileSpansResult, StateSpanCategory.Profile);
        var overrideSpanDtos = MapStateSpans(overrideSpansResult, StateSpanCategory.Override);

        var activitySpanDtos = new List<ChartStateSpanDto>();
        activitySpanDtos.AddRange(MapStateSpans(sleepSpansResult, StateSpanCategory.Sleep));
        activitySpanDtos.AddRange(MapStateSpans(exerciseSpansResult, StateSpanCategory.Exercise));
        activitySpanDtos.AddRange(MapStateSpans(illnessSpansResult, StateSpanCategory.Illness));
        activitySpanDtos.AddRange(MapStateSpans(travelSpansResult, StateSpanCategory.Travel));

        var basalDeliverySpanDtos = MapBasalDeliverySpans(basalDeliverySpans, defaultBasalRate);
        var tempBasalSpanDtos = MapTempBasalSpans(basalDeliverySpans, defaultBasalRate);
        var systemEventDtos = MapSystemEvents(systemEventsResult);
        var trackerMarkers = MapTrackerMarkers(trackerDefs, trackerInstances, startTime, endTime);

        return new DashboardChartData
        {
            IobSeries = iobSeries,
            CobSeries = cobSeries,
            BasalSeries = basalSeries,
            DefaultBasalRate = defaultBasalRate,
            MaxBasalRate = maxBasalRate,
            MaxIob = Math.Max(3, maxIob),
            MaxCob = Math.Max(30, maxCob),

            GlucoseData = glucoseData,
            Thresholds = thresholds with { GlucoseYMax = glucoseYMax },

            BolusMarkers = bolusMarkers,
            CarbMarkers = carbMarkers,
            DeviceEventMarkers = deviceEventMarkers,

            PumpModeSpans = pumpModeSpanDtos,
            ProfileSpans = profileSpanDtos,
            OverrideSpans = overrideSpanDtos,
            ActivitySpans = activitySpanDtos,
            TempBasalSpans = tempBasalSpanDtos,
            BasalDeliverySpans = basalDeliverySpanDtos,

            SystemEventMarkers = systemEventDtos,
            TrackerMarkers = trackerMarkers,
        };
    }

    #region Internal Helpers

    internal ChartThresholdsDto GetProfileThresholds(long time)
    {
        if (!_profileService.HasData())
        {
            return new ChartThresholdsDto
            {
                VeryLow = DefaultVeryLow,
                Low = 70,
                High = 180,
                VeryHigh = DefaultVeryHigh,
            };
        }

        return new ChartThresholdsDto
        {
            VeryLow = DefaultVeryLow,
            Low = _profileService.GetLowBGTarget(time, null),
            High = _profileService.GetHighBGTarget(time, null),
            VeryHigh = DefaultVeryHigh,
        };
    }

    internal (
        List<TimeSeriesPoint> iobSeries,
        List<TimeSeriesPoint> cobSeries,
        double maxIob,
        double maxCob
    ) BuildIobCobSeries(
        List<Treatment> treatments,
        List<DeviceStatus> deviceStatuses,
        long startTime,
        long endTime,
        int intervalMinutes
    )
    {
        var iobSeries = new List<TimeSeriesPoint>();
        var cobSeries = new List<TimeSeriesPoint>();
        var intervalMs = intervalMinutes * 60 * 1000;
        double maxIob = 0,
            maxCob = 0;

        for (long t = startTime; t <= endTime; t += intervalMs)
        {
            var iobResult = _iobService.FromTreatments(
                treatments,
                _profileService.HasData() ? _profileService : null,
                t,
                null
            );
            var iob = iobResult.Iob;
            iobSeries.Add(new TimeSeriesPoint { Timestamp = t, Value = iob });
            if (iob > maxIob)
                maxIob = iob;

            var cobResult = _cobService.CobTotal(
                treatments,
                deviceStatuses,
                _profileService.HasData() ? _profileService : null,
                t,
                null
            );
            var cob = cobResult.Cob;
            cobSeries.Add(new TimeSeriesPoint { Timestamp = t, Value = cob });
            if (cob > maxCob)
                maxCob = cob;
        }

        return (iobSeries, cobSeries, maxIob, maxCob);
    }

    internal static (List<GlucosePointDto> data, double yMax) BuildGlucoseData(List<Entry> entries)
    {
        var glucoseData = entries
            .Where(e => e.Sgv != null)
            .Select(e => new GlucosePointDto
            {
                Time = e.Mills,
                Sgv = e.Sgv ?? 0,
                Direction = e.Direction,
            })
            .OrderBy(g => g.Time)
            .ToList();

        var maxSgv = glucoseData.Any() ? glucoseData.Max(g => g.Sgv) : 280;
        var glucoseYMax = Math.Min(400, Math.Max(280, maxSgv) + 20);

        return (glucoseData, glucoseYMax);
    }

    internal static (
        List<BolusMarkerDto> bolus,
        List<CarbMarkerDto> carbs,
        List<DeviceEventMarkerDto> deviceEvents,
        List<Guid> carbTreatmentIds
    ) CategorizeTreatments(List<Treatment> treatments, string? timezone)
    {
        var bolusMarkers = new List<BolusMarkerDto>();
        var carbMarkers = new List<CarbMarkerDto>();
        var deviceEventMarkers = new List<DeviceEventMarkerDto>();
        var carbTreatmentIds = new List<Guid>();

        foreach (var t in treatments)
        {
            // Bolus check
            if (t.Insulin is > 0)
            {
                var eventType = t.EventType ?? "";
                if (
                    TreatmentTypes.BolusEventTypeMap.TryGetValue(eventType, out var bolusType)
                    || eventType.Contains("Bolus", StringComparison.OrdinalIgnoreCase)
                )
                {
                    var suggestedTotal =
                        (t.InsulinRecommendationForCarbs ?? 0)
                        + (t.InsulinRecommendationForCorrection ?? 0);
                    var isOverride =
                        suggestedTotal > 0 && Math.Abs(suggestedTotal - (t.Insulin ?? 0)) > 0.05;

                    bolusMarkers.Add(
                        new BolusMarkerDto
                        {
                            Time = t.Mills,
                            Insulin = t.Insulin ?? 0,
                            TreatmentId = t.Id,
                            BolusType = bolusType,
                            IsOverride = isOverride,
                        }
                    );
                }
            }

            // Carb check
            if (t.Carbs is > 0)
            {
                if (t.Id != null && Guid.TryParse(t.Id, out var treatmentGuid))
                    carbTreatmentIds.Add(treatmentGuid);

                carbMarkers.Add(
                    new CarbMarkerDto
                    {
                        Time = t.Mills,
                        Carbs = t.Carbs ?? 0,
                        Label =
                            t.FoodType
                            ?? (t.Notes != null ? t.Notes[..Math.Min(t.Notes.Length, 20)] : null)
                            ?? GetMealNameForTime(t.Mills, timezone),
                        TreatmentId = t.Id,
                        IsOffset = false,
                    }
                );
            }

            // Device event check
            if (
                t.EventType != null
                && TreatmentTypes.DeviceEventTypeMap.TryGetValue(
                    t.EventType,
                    out var deviceEventType
                )
            )
            {
                deviceEventMarkers.Add(
                    new DeviceEventMarkerDto
                    {
                        Time = t.Mills,
                        EventType = deviceEventType,
                        Notes = t.Notes,
                        Color = ChartColorMapper.FromDeviceEvent(deviceEventType),
                    }
                );
            }
        }

        return (bolusMarkers, carbMarkers, deviceEventMarkers, carbTreatmentIds);
    }

    internal async Task ProcessFoodOffsetsAsync(
        List<CarbMarkerDto> carbMarkers,
        List<Guid> carbTreatmentIds,
        List<Treatment> displayTreatments,
        CancellationToken cancellationToken
    )
    {
        if (carbTreatmentIds.Count == 0)
            return;

        var foods = (
            await _treatmentFoodService.GetByTreatmentIdsAsync(carbTreatmentIds, cancellationToken)
        ).ToList();

        if (foods.Count == 0)
            return;

        var foodsByTreatment = foods
            .GroupBy(f => f.TreatmentId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (treatmentId, treatmentFoods) in foodsByTreatment)
        {
            var offsetFoods = treatmentFoods.Where(f => f.TimeOffsetMinutes != 0).ToList();

            if (offsetFoods.Count == 0)
                continue;

            var baseTreatment = displayTreatments.FirstOrDefault(t =>
                t.Id == treatmentId.ToString()
            );
            if (baseTreatment == null)
                continue;

            var baseMills = baseTreatment.Mills;
            var offsetGroups = offsetFoods.GroupBy(f => f.TimeOffsetMinutes).ToList();

            foreach (var group in offsetGroups)
            {
                var offsetMs = group.Key * 60 * 1000;
                var offsetTime = baseMills + offsetMs;
                var totalCarbs = group.Sum(f => (double)f.Carbs);
                var labels = group.Where(f => f.FoodName != null).Select(f => f.FoodName!).ToList();
                var label =
                    labels.Count > 0
                        ? string.Join(", ", labels)[
                            ..Math.Min(string.Join(", ", labels).Length, 20)
                        ]
                        : null;

                carbMarkers.Add(
                    new CarbMarkerDto
                    {
                        Time = offsetTime,
                        Carbs = totalCarbs,
                        Label = label,
                        TreatmentId = baseTreatment.Id,
                        IsOffset = true,
                    }
                );
            }

            // Update base marker label with base food names
            var baseFoods = treatmentFoods.Where(f => f.TimeOffsetMinutes == 0).ToList();
            if (baseFoods.Count > 0)
            {
                var baseLabels = baseFoods
                    .Where(f => f.FoodName != null)
                    .Select(f => f.FoodName!)
                    .ToList();
                if (baseLabels.Count > 0)
                {
                    var baseMarker = carbMarkers.FirstOrDefault(m =>
                        m.TreatmentId == baseTreatment.Id && !m.IsOffset
                    );
                    if (baseMarker != null)
                    {
                        var joined = string.Join(", ", baseLabels);
                        baseMarker.Label = joined[..Math.Min(joined.Length, 20)];
                    }
                }
            }
        }
    }

    internal List<BasalDeliverySpanDto> MapBasalDeliverySpans(
        List<StateSpan> basalDeliverySpans,
        double defaultBasalRate
    )
    {
        return basalDeliverySpans
            .Select(span =>
            {
                var (rate, origin) = ExtractBasalDeliveryMetadata(span, defaultBasalRate);
                return new BasalDeliverySpanDto
                {
                    Id = span.Id ?? "",
                    StartMills = span.StartMills,
                    EndMills = span.EndMills,
                    Rate = origin == BasalDeliveryOrigin.Suspended ? 0 : rate,
                    Origin = origin,
                    Source = span.Source,
                    FillColor = ChartColorMapper.FillFromBasalOrigin(origin),
                    StrokeColor = ChartColorMapper.StrokeFromBasalOrigin(origin),
                };
            })
            .ToList();
    }

    internal List<ChartStateSpanDto> MapTempBasalSpans(
        List<StateSpan> basalDeliverySpans,
        double defaultBasalRate
    )
    {
        return basalDeliverySpans
            .Where(span =>
            {
                var (_, origin) = ExtractBasalDeliveryMetadata(span, defaultBasalRate);
                return origin == BasalDeliveryOrigin.Manual;
            })
            .Select(span => new ChartStateSpanDto
            {
                Id = span.Id ?? "",
                Category = StateSpanCategory.BasalDelivery,
                State = "TempBasal",
                StartMills = span.StartMills,
                EndMills = span.EndMills,
                Color = ChartColor.InsulinBasal,
                Metadata = span.Metadata,
            })
            .ToList();
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

    internal List<ChartStateSpanDto> MapStateSpans(
        IEnumerable<StateSpan> spans,
        StateSpanCategory category
    )
    {
        return spans
            .Select(span => new ChartStateSpanDto
            {
                Id = span.Id ?? "",
                Category = category,
                State = span.State ?? "Unknown",
                StartMills = span.StartMills,
                EndMills = span.EndMills,
                Color = category switch
                {
                    StateSpanCategory.PumpMode => ChartColorMapper.FromPumpMode(span.State ?? ""),
                    StateSpanCategory.Override => ChartColorMapper.FromOverride(span.State ?? ""),
                    StateSpanCategory.Profile => ChartColor.Profile,
                    StateSpanCategory.Sleep
                    or StateSpanCategory.Exercise
                    or StateSpanCategory.Illness
                    or StateSpanCategory.Travel => ChartColorMapper.FromActivity(category),
                    _ => ChartColor.MutedForeground,
                },
                Metadata = span.Metadata,
            })
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
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
                time = TimeZoneInfo.ConvertTime(time, tz);
            }
            catch
            {
                // Fall back to UTC if timezone conversion fails
            }
        }
        return time.Hour switch
        {
            >= 5 and < 11 => "Breakfast",
            >= 11 and < 15 => "Lunch",
            >= 15 and < 17 => "Snack",
            >= 17 and < 21 => "Dinner",
            _ => "Late Night",
        };
    }

    /// <summary>
    /// Build basal series from BasalDelivery StateSpans.
    /// StateSpans are the source of truth for pump-confirmed delivery.
    /// Falls back to profile-based rates when there are gaps in StateSpan data.
    /// </summary>
    internal List<BasalPoint> BuildBasalSeriesFromStateSpans(
        List<StateSpan> basalDeliverySpans,
        long startTime,
        long endTime,
        double defaultBasalRate
    )
    {
        var series = new List<BasalPoint>();
        var sortedSpans = basalDeliverySpans.OrderBy(s => s.StartMills).ToList();

        _logger.LogDebug(
            "Building basal series from {SpanCount} BasalDelivery StateSpans",
            sortedSpans.Count
        );

        if (sortedSpans.Count == 0)
            return BuildBasalSeriesFromProfile(startTime, endTime, defaultBasalRate);

        long currentTime = startTime;

        foreach (var span in sortedSpans)
        {
            var spanStart = span.StartMills;
            var spanEnd = span.EndMills ?? endTime;

            if (spanEnd < startTime || spanStart > endTime)
                continue;

            spanStart = Math.Max(spanStart, startTime);
            spanEnd = Math.Min(spanEnd, endTime);

            if (spanStart > currentTime)
            {
                series.AddRange(
                    BuildBasalSeriesFromProfile(currentTime, spanStart, defaultBasalRate)
                );
            }

            var (rate, origin) = ExtractBasalDeliveryMetadata(span, defaultBasalRate);

            var scheduledRate = _profileService.HasData()
                ? _profileService.GetBasalRate(spanStart, null)
                : defaultBasalRate;

            series.Add(
                new BasalPoint
                {
                    Timestamp = spanStart,
                    Rate = origin == BasalDeliveryOrigin.Suspended ? 0 : rate,
                    ScheduledRate = scheduledRate,
                    Origin = origin,
                    FillColor = ChartColorMapper.FillFromBasalOrigin(origin),
                    StrokeColor = ChartColorMapper.StrokeFromBasalOrigin(origin),
                }
            );

            currentTime = spanEnd;
        }

        if (currentTime < endTime)
            series.AddRange(BuildBasalSeriesFromProfile(currentTime, endTime, defaultBasalRate));

        if (series.Count == 0)
        {
            series.Add(
                new BasalPoint
                {
                    Timestamp = startTime,
                    Rate = defaultBasalRate,
                    ScheduledRate = defaultBasalRate,
                    Origin = BasalDeliveryOrigin.Scheduled,
                    FillColor = ChartColorMapper.FillFromBasalOrigin(BasalDeliveryOrigin.Scheduled),
                    StrokeColor = ChartColorMapper.StrokeFromBasalOrigin(
                        BasalDeliveryOrigin.Scheduled
                    ),
                }
            );
        }

        return series;
    }

    internal List<BasalPoint> BuildBasalSeriesFromProfile(
        long startTime,
        long endTime,
        double defaultBasalRate
    )
    {
        var series = new List<BasalPoint>();
        const long intervalMs = 5 * 60 * 1000;
        double? prevRate = null;

        for (long t = startTime; t <= endTime; t += intervalMs)
        {
            var rate = _profileService.HasData()
                ? _profileService.GetBasalRate(t, null)
                : defaultBasalRate;

            if (prevRate == null || Math.Abs(rate - prevRate.Value) > 0.001)
            {
                series.Add(
                    new BasalPoint
                    {
                        Timestamp = t,
                        Rate = rate,
                        ScheduledRate = rate,
                        Origin = BasalDeliveryOrigin.Inferred,
                        FillColor = ChartColorMapper.FillFromBasalOrigin(
                            BasalDeliveryOrigin.Inferred
                        ),
                        StrokeColor = ChartColorMapper.StrokeFromBasalOrigin(
                            BasalDeliveryOrigin.Inferred
                        ),
                    }
                );
                prevRate = rate;
            }
        }

        if (series.Count == 0)
        {
            series.Add(
                new BasalPoint
                {
                    Timestamp = startTime,
                    Rate = defaultBasalRate,
                    ScheduledRate = defaultBasalRate,
                    Origin = BasalDeliveryOrigin.Inferred,
                    FillColor = ChartColorMapper.FillFromBasalOrigin(BasalDeliveryOrigin.Inferred),
                    StrokeColor = ChartColorMapper.StrokeFromBasalOrigin(
                        BasalDeliveryOrigin.Inferred
                    ),
                }
            );
        }

        return series;
    }

    #endregion
}
