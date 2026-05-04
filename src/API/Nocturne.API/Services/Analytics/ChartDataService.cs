using System.Text.Json;
using Nocturne.API.Helpers;
using Nocturne.API.Services.ChartData;
using Nocturne.Core.Contracts.Analytics;
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
    private readonly IEnumerable<IChartDataStage> _pipeline;
    private readonly IChartDataAssembler _assembler;
    private readonly ILogger<ChartDataService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ChartDataService"/>.
    /// </summary>
    /// <param name="pipeline">The ordered sequence of <see cref="IChartDataStage"/> stages to execute.</param>
    /// <param name="assembler">The assembler that converts the completed context into the final DTO.</param>
    /// <param name="logger">The logger instance.</param>
    public ChartDataService(
        IEnumerable<IChartDataStage> pipeline,
        IChartDataAssembler assembler,
        ILogger<ChartDataService> logger
    )
    {
        _pipeline = pipeline;
        _assembler = assembler;
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
        const long bufferMs = 8L * 60 * 60 * 1000;
        var context = new ChartDataContext
        {
            StartTime = startTime,
            EndTime = endTime,
            IntervalMinutes = intervalMinutes,
            BufferStartTime = startTime - bufferMs,
        };

        foreach (var stage in _pipeline)
        {
            context = await stage.ExecuteAsync(context, cancellationToken);
        }

        return _assembler.Assemble(context);
    }

    #region Internal Helpers

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
                Direction = r.Direction?.ToString(),
                DataSource = r.DataSource ?? r.Device,
            })
            .ToList(); // Already sorted

        var maxSgv = glucoseData.Any() ? glucoseData.Max(g => g.Sgv) : 280;
        var glucoseYMax = Math.Min(400, Math.Max(280, maxSgv) + 20);

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
    /// Builds lightweight Treatment adapter objects from v4 Bolus and CarbIntake data.
    /// The IOB/COB calculation services (IIobService, ICobService) are deeply coupled to the
    /// legacy Treatment type through their interfaces. Rather than rewriting those calculation
    /// engines (which implement exact 1:1 legacy JavaScript algorithm compatibility), we build
    /// thin Treatment objects containing only the fields the calculations actually use:
    ///   - IOB: Treatment.Mills, Treatment.Insulin, Treatment.EventType ("Temp Basal"),
    ///          Treatment.Duration, Treatment.Absolute
    ///   - COB: Treatment.Mills, Treatment.Carbs, Treatment.Notes
    /// </summary>
    internal static List<Treatment> BuildTreatmentsFromV4Data(
        List<Bolus> boluses,
        List<CarbIntake> carbIntakes,
        IReadOnlyDictionary<Guid, List<TreatmentFood>> foodsByCarbIntake
    )
    {
        var treatments = new List<Treatment>(boluses.Count + carbIntakes.Count);

        foreach (var bolus in boluses)
        {
            if (bolus.Insulin <= 0)
                continue;

            treatments.Add(
                new Treatment
                {
                    Id = bolus.LegacyId ?? bolus.Id.ToString(),
                    Mills = bolus.Mills,
                    Insulin = bolus.Insulin,
                    InsulinContext = bolus.InsulinContext,
                }
            );
        }

        foreach (var carb in carbIntakes)
        {
            if (carb.Carbs <= 0)
                continue;

            double? totalFat = null;
            if (foodsByCarbIntake.TryGetValue(carb.Id, out var foods))
            {
                var sum = foods
                    .Where(f => f.FatPerPortion.HasValue && f.Portions > 0)
                    .Sum(f => (double)(f.FatPerPortion!.Value * f.Portions));
                if (sum > 0)
                    totalFat = sum;
            }

            treatments.Add(
                new Treatment
                {
                    Id = carb.LegacyId ?? carb.Id.ToString(),
                    Mills = carb.Mills,
                    Carbs = carb.Carbs,
                    Fat = totalFat,
                    AbsorptionTime = carb.AbsorptionTime,
                }
            );
        }

        return treatments;
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
        return tempBasals
            .Select(tb =>
            {
                var origin = MapTempBasalOrigin(tb.Origin);
                return new BasalDeliverySpanDto
                {
                    Id = tb.LegacyId ?? tb.Id.ToString(),
                    StartMills = tb.StartMills,
                    EndMills = tb.EndMills,
                    Rate = origin == BasalDeliveryOrigin.Suspended ? 0 : tb.Rate,
                    Origin = origin,
                    Source = tb.DataSource,
                    FillColor = ChartColorMapper.FillFromBasalOrigin(origin),
                    StrokeColor = ChartColorMapper.StrokeFromBasalOrigin(origin),
                };
            })
            .ToList();
    }

    internal static List<ChartStateSpanDto> MapTempBasalSpans(
        List<TempBasal> tempBasals
    )
    {
        return tempBasals
            .Where(tb => tb.Origin == TempBasalOrigin.Manual)
            .Select(tb => new ChartStateSpanDto
            {
                Id = tb.LegacyId ?? tb.Id.ToString(),
                Category = StateSpanCategory.PumpMode, // Rendered in dedicated tempBasalSpans list; category is informational
                State = "TempBasal",
                StartMills = tb.StartMills,
                EndMills = tb.EndMills,
                Color = ChartColor.InsulinBasal,
                Metadata = null,
            })
            .ToList();
    }

    /// <summary>
    /// Maps a TempBasalOrigin enum value to the corresponding BasalDeliveryOrigin enum value.
    /// Both enums have identical members (Algorithm, Scheduled, Manual, Suspended, Inferred).
    /// </summary>
    internal static BasalDeliveryOrigin MapTempBasalOrigin(TempBasalOrigin origin) =>
        origin switch
        {
            TempBasalOrigin.Algorithm => BasalDeliveryOrigin.Algorithm,
            TempBasalOrigin.Scheduled => BasalDeliveryOrigin.Scheduled,
            TempBasalOrigin.Manual => BasalDeliveryOrigin.Manual,
            TempBasalOrigin.Suspended => BasalDeliveryOrigin.Suspended,
            TempBasalOrigin.Inferred => BasalDeliveryOrigin.Inferred,
            _ => BasalDeliveryOrigin.Scheduled,
        };

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

    #endregion
}
