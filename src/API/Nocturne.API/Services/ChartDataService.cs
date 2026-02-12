using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
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
    private readonly IMemoryCache _cache;
    private readonly ILogger<ChartDataService> _logger;

    // Clinical standard thresholds (mg/dL) -- used when profile doesn't specify
    private const double DefaultVeryLow = 54;
    private const double DefaultVeryHigh = 250;

    // Cache settings
    private static readonly TimeSpan IobCobCacheExpiration = TimeSpan.FromMinutes(1);

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
        IMemoryCache cache,
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
        _cache = cache;
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

        // Calculate reasonable limits based on the actual time range
        var rangeHours = (endTime - startTime) / (60.0 * 60 * 1000);
        // At 5-min CGM intervals: ~12 entries/hour. Add 50% safety margin.
        var entryLimit = (int)Math.Max(500, Math.Ceiling(rangeHours * 12 * 1.5));
        // Treatments are less frequent but include the buffer window
        var treatmentRangeHours = (endTime - (startTime - bufferMs)) / (60.0 * 60 * 1000);
        var treatmentLimit = (int)Math.Max(500, Math.Ceiling(treatmentRangeHours * 10));

        var treatmentFind = $"{{\"mills\":{{\"$gte\":{startTime - bufferMs},\"$lte\":{endTime}}}}}";
        var relevantTreatments =
            (
                await _treatmentService.GetTreatmentsAsync(
                    find: treatmentFind,
                    count: treatmentLimit,
                    cancellationToken: cancellationToken
                )
            )?.ToList() ?? new List<Treatment>();

        var entryFind = $"{{\"mills\":{{\"$gte\":{startTime},\"$lte\":{endTime}}}}}";
        var relevantEntries =
            (
                await _entryService.GetEntriesAsync(
                    find: entryFind,
                    count: entryLimit,
                    cancellationToken: cancellationToken
                )
            )?.ToList() ?? new List<Entry>();

        // Device status - only need recent entries for IOB source detection
        var deviceStatusList =
            (
                await _deviceStatusService.GetDeviceStatusAsync(
                    count: 100,
                    skip: 0,
                    cancellationToken: cancellationToken
                )
            )?.ToList() ?? new List<DeviceStatus>();

        var displayTreatments = relevantTreatments
            .Where(t => t.Mills >= startTime && t.Mills <= endTime)
            .ToList();

        // Fetch all state spans in a single batched query
        var stateSpanCategories = new[]
        {
            StateSpanCategory.PumpMode,
            StateSpanCategory.BasalDelivery,
            StateSpanCategory.Profile,
            StateSpanCategory.Override,
            StateSpanCategory.Sleep,
            StateSpanCategory.Exercise,
            StateSpanCategory.Illness,
            StateSpanCategory.Travel,
        };

        var allStateSpans = await _stateSpanRepository.GetByCategories(
            stateSpanCategories,
            startTime,
            endTime,
            cancellationToken
        );

        var pumpModeSpansResult = allStateSpans[StateSpanCategory.PumpMode];
        var basalDeliverySpansResult = allStateSpans[StateSpanCategory.BasalDelivery];
        var profileSpansResult = allStateSpans[StateSpanCategory.Profile];
        var overrideSpansResult = allStateSpans[StateSpanCategory.Override];
        var sleepSpansResult = allStateSpans[StateSpanCategory.Sleep];
        var exerciseSpansResult = allStateSpans[StateSpanCategory.Exercise];
        var illnessSpansResult = allStateSpans[StateSpanCategory.Illness];
        var travelSpansResult = allStateSpans[StateSpanCategory.Travel];

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
        // Generate cache key based on treatment data hash and time range
        var cacheKey = GenerateIobCobCacheKey(treatments, startTime, endTime, intervalMinutes);

        // Try to get from cache
        if (
            _cache.TryGetValue(
                cacheKey,
                out (
                    List<TimeSeriesPoint> iob,
                    List<TimeSeriesPoint> cob,
                    double maxIob,
                    double maxCob
                ) cached
            )
        )
        {
            _logger.LogDebug("IOB/COB cache hit for range {Start}-{End}", startTime, endTime);
            return cached;
        }

        _logger.LogDebug(
            "IOB/COB cache miss, computing for range {Start}-{End}",
            startTime,
            endTime
        );

        var iobSeries = new List<TimeSeriesPoint>();
        var cobSeries = new List<TimeSeriesPoint>();
        var intervalMs = intervalMinutes * 60 * 1000;
        double maxIob = 0,
            maxCob = 0;

        // Pre-compute DIA and COB absorption window for filtering
        var dia = _profileService.HasData() ? _profileService.GetDIA(endTime, null) : 3.0;
        var diaMs = (long)(dia * 60 * 60 * 1000); // DIA in milliseconds
        var cobAbsorptionMs = 6L * 60 * 60 * 1000; // 6 hours for COB absorption

        // Pre-filter treatments with insulin for IOB calculations
        var insulinTreatments = treatments
            .Where(t => t.Insulin.HasValue && t.Insulin.Value > 0)
            .ToList();

        // Pre-filter treatments with carbs for COB calculations
        var carbTreatments = treatments.Where(t => t.Carbs.HasValue && t.Carbs.Value > 0).ToList();

        var profile = _profileService.HasData() ? _profileService : null;

        for (long t = startTime; t <= endTime; t += intervalMs)
        {
            // Filter to only treatments that could still have active IOB at time t
            // A treatment can only contribute IOB if it was given within DIA hours before t
            var relevantIobTreatments = insulinTreatments
                .Where(tr => tr.Mills <= t && tr.Mills >= t - diaMs)
                .ToList();

            var iobResult =
                relevantIobTreatments.Count > 0
                    ? _iobService.FromTreatments(relevantIobTreatments, profile, t, null)
                    : new IobResult { Iob = 0 };

            var iob = iobResult.Iob;
            iobSeries.Add(new TimeSeriesPoint { Timestamp = t, Value = iob });
            if (iob > maxIob)
                maxIob = iob;

            // Filter to only treatments that could still have active COB at time t
            var relevantCobTreatments = carbTreatments
                .Where(tr => tr.Mills <= t && tr.Mills >= t - cobAbsorptionMs)
                .ToList();

            var cobResult =
                relevantCobTreatments.Count > 0
                    ? _cobService.CobTotal(relevantCobTreatments, deviceStatuses, profile, t, null)
                    : new CobResult { Cob = 0 };

            var cob = cobResult.Cob;
            cobSeries.Add(new TimeSeriesPoint { Timestamp = t, Value = cob });
            if (cob > maxCob)
                maxCob = cob;
        }

        // Cache the result
        var result = (iobSeries, cobSeries, maxIob, maxCob);
        _cache.Set(cacheKey, result, IobCobCacheExpiration);

        return result;
    }

    /// <summary>
    /// Generate a cache key for IOB/COB calculations based on treatment fingerprint and time range.
    /// Uses SHA256 of individual treatment mills/insulin/carbs values for collision resistance.
    /// </summary>
    private static string GenerateIobCobCacheKey(
        List<Treatment> treatments,
        long startTime,
        long endTime,
        int intervalMinutes
    )
    {
        // Round start/end times to interval boundaries for better cache hits
        var intervalMs = intervalMinutes * 60 * 1000;
        var roundedStart = (startTime / intervalMs) * intervalMs;
        var roundedEnd = (endTime / intervalMs) * intervalMs;

        // Hash individual treatment data for a collision-resistant fingerprint
        var sb = new StringBuilder();
        foreach (var t in treatments)
        {
            if (
                (t.Insulin.HasValue && t.Insulin.Value > 0)
                || (t.Carbs.HasValue && t.Carbs.Value > 0)
            )
            {
                sb.Append(t.Mills)
                    .Append(':')
                    .Append(t.Insulin ?? 0)
                    .Append(':')
                    .Append(t.Carbs ?? 0)
                    .Append('|');
            }
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[
            ..16
        ]; // First 16 hex chars (64 bits) is sufficient

        return $"iobcob:{hash}:{roundedStart}:{roundedEnd}:{intervalMinutes}";
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
