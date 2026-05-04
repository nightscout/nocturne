using Nocturne.API.Helpers;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.ChartData.Stages;

/// <summary>
/// Chart data pipeline stage that converts raw fetched domain objects into chart-ready DTOs.
/// Builds glucose series, all marker types, state-span series, basal delivery spans, and tracker markers.
/// </summary>
/// <remarks>
/// <para>
/// Carb markers receive a secondary enrichment pass: foods with a non-zero
/// <c>TimeOffsetMinutes</c> are emitted as additional <see cref="CarbMarkerDto"/> entries
/// offset from the parent <c>CarbIntake.Mills</c>, capped to 20 characters for label display.
/// Base markers (offset == 0) have their label updated from the associated food names.
/// </para>
/// <para>
/// Activity state spans from Sleep, Exercise, Illness, and Travel categories are merged into
/// a single <see cref="ChartDataContext.ActivitySpans"/> list so they share one chart layer.
/// </para>
/// <para>
/// Color assignment for state spans is performed by <see cref="Helpers.ChartColorMapper"/>
/// on the backend so the rendering layer is a pure display layer.
/// </para>
/// </remarks>
/// <seealso cref="IChartDataStage"/>
/// <seealso cref="ChartDataContext"/>
internal sealed class DtoMappingStage(ITreatmentFoodService treatmentFoodService) : IChartDataStage
{
    public async Task<ChartDataContext> ExecuteAsync(ChartDataContext context, CancellationToken cancellationToken)
    {
        var (glucoseData, glucoseYMax) = ChartDataService.BuildGlucoseData(context.SensorGlucoseList.ToList());

        var bolusMarkers = ChartDataService.BuildBolusMarkers(context.DisplayBoluses.ToList());
        var carbMarkers = ChartDataService.BuildCarbMarkers(context.DisplayCarbIntakes.ToList(), context.Timezone);
        var bgCheckMarkers = ChartDataService.BuildBgCheckMarkers(context.BgCheckList.ToList());
        var deviceEventMarkers = ChartDataService.BuildDeviceEventMarkers(context.DeviceEventList.ToList());

        var carbIntakeIds = context.DisplayCarbIntakes.Select(c => c.Id).Distinct().ToList();
        await ProcessFoodOffsetsAsync(carbMarkers, carbIntakeIds, context.DisplayCarbIntakes.ToList(), cancellationToken);

        var pumpModeSpans = context.StateSpans.TryGetValue(StateSpanCategory.PumpMode, out var pumpModeRaw)
            ? MapStateSpans(pumpModeRaw, StateSpanCategory.PumpMode)
            : [];
        var profileSpans = context.StateSpans.TryGetValue(StateSpanCategory.Profile, out var profileRaw)
            ? MapStateSpans(profileRaw, StateSpanCategory.Profile)
            : [];
        var overrideSpans = context.StateSpans.TryGetValue(StateSpanCategory.Override, out var overrideRaw)
            ? MapStateSpans(overrideRaw, StateSpanCategory.Override)
            : [];

        var activitySpans = new List<ChartStateSpanDto>();
        if (context.StateSpans.TryGetValue(StateSpanCategory.Sleep, out var sleepRaw))
            activitySpans.AddRange(MapStateSpans(sleepRaw, StateSpanCategory.Sleep));
        if (context.StateSpans.TryGetValue(StateSpanCategory.Exercise, out var exerciseRaw))
            activitySpans.AddRange(MapStateSpans(exerciseRaw, StateSpanCategory.Exercise));
        if (context.StateSpans.TryGetValue(StateSpanCategory.Illness, out var illnessRaw))
            activitySpans.AddRange(MapStateSpans(illnessRaw, StateSpanCategory.Illness));
        if (context.StateSpans.TryGetValue(StateSpanCategory.Travel, out var travelRaw))
            activitySpans.AddRange(MapStateSpans(travelRaw, StateSpanCategory.Travel));

        var basalDeliverySpans = ChartDataService.MapBasalDeliverySpans(context.TempBasalList.ToList());
        var tempBasalSpans = ChartDataService.MapTempBasalSpans(context.TempBasalList.ToList());
        var systemEventMarkers = ChartDataService.MapSystemEvents(context.SystemEvents);
        var trackerMarkers = ChartDataService.MapTrackerMarkers(
            context.TrackerDefinitions,
            context.TrackerInstances,
            context.StartTime,
            context.EndTime
        );

        var heartRateSeries = context.HeartRateList
            .Select(hr => new HeartRatePointDto { Time = hr.Mills, Bpm = hr.Bpm })
            .OrderBy(p => p.Time)
            .ToList();

        var stepSeries = context.StepCountList
            .Where(sc => sc.Metric > 0)
            .Select(sc => new StepBubbleDto { Time = sc.Mills, Steps = sc.Metric })
            .OrderBy(p => p.Time)
            .ToList();

        return context with
        {
            GlucoseData = glucoseData,
            GlucoseYMax = glucoseYMax,
            BolusMarkers = bolusMarkers,
            CarbMarkers = carbMarkers,
            BgCheckMarkers = bgCheckMarkers,
            DeviceEventMarkers = deviceEventMarkers,
            PumpModeSpans = pumpModeSpans,
            ProfileSpans = profileSpans,
            OverrideSpans = overrideSpans,
            ActivitySpans = activitySpans,
            BasalDeliverySpans = basalDeliverySpans,
            TempBasalSpans = tempBasalSpans,
            SystemEventMarkers = systemEventMarkers,
            TrackerMarkers = trackerMarkers,
            HeartRateSeries = heartRateSeries,
            StepSeries = stepSeries,
        };
    }

    private static List<ChartStateSpanDto> MapStateSpans(IEnumerable<StateSpan> spans, StateSpanCategory category)
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

    private async Task ProcessFoodOffsetsAsync(
        List<CarbMarkerDto> carbMarkers,
        List<Guid> carbIntakeIds,
        List<CarbIntake> displayCarbIntakes,
        CancellationToken cancellationToken
    )
    {
        if (carbIntakeIds.Count == 0)
            return;

        var foods = (
            await treatmentFoodService.GetByCarbIntakeIdsAsync(carbIntakeIds, cancellationToken)
        ).ToList();

        if (foods.Count == 0)
            return;

        var foodsByCarbIntake = foods
            .GroupBy(f => f.CarbIntakeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var carbIntakeById = displayCarbIntakes.ToDictionary(c => c.Id, c => c);

        foreach (var (carbIntakeId, carbIntakeFoods) in foodsByCarbIntake)
        {
            var offsetFoods = carbIntakeFoods.Where(f => f.TimeOffsetMinutes != 0).ToList();

            if (offsetFoods.Count == 0)
                continue;

            if (!carbIntakeById.TryGetValue(carbIntakeId, out var baseCarbIntake))
                continue;

            var baseMills = baseCarbIntake.Mills;
            var baseId = baseCarbIntake.LegacyId ?? baseCarbIntake.Id.ToString();
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
                        TreatmentId = baseId,
                        IsOffset = true,
                    }
                );
            }

            // Update base marker label with base food names
            var baseFoods = carbIntakeFoods.Where(f => f.TimeOffsetMinutes == 0).ToList();
            if (baseFoods.Count > 0)
            {
                var baseLabels = baseFoods
                    .Where(f => f.FoodName != null)
                    .Select(f => f.FoodName!)
                    .ToList();
                if (baseLabels.Count > 0)
                {
                    var baseMarker = carbMarkers.FirstOrDefault(m =>
                        m.TreatmentId == baseId && !m.IsOffset
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
}
