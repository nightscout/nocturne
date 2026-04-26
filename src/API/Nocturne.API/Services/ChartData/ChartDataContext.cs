using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.ChartData;

/// <summary>
/// Immutable data envelope passed sequentially through each <see cref="IChartDataStage"/>
/// in the chart data pipeline. Each stage produces a new instance via <see langword="with"/>
/// expressions and leaves the previous instance unchanged.
/// </summary>
/// <remarks>
/// Stage ordering and responsibilities:
/// <list type="number">
///   <item><see cref="Stages.ProfileLoadStage"/> — populates <see cref="Timezone"/>, <see cref="Thresholds"/>, and <see cref="DefaultBasalRate"/>.</item>
///   <item><see cref="Stages.DataFetchStage"/> — populates all raw data collections.</item>
///   <item><see cref="Stages.TreatmentAdapterStage"/> — builds <see cref="SyntheticTreatments"/> from v4 Bolus/CarbIntake records for legacy IOB/COB calcs.</item>
///   <item><see cref="Stages.IobCobComputeStage"/> — computes IOB/COB series and basal series, with 1-minute memory cache keyed by tenant + data fingerprint.</item>
///   <item><see cref="Stages.DtoMappingStage"/> — converts raw data to chart-ready DTOs (markers, spans, glucose points).</item>
/// </list>
/// All timestamp properties use Unix milliseconds to match the mills-first domain convention.
/// </remarks>
public sealed record ChartDataContext
{
    // === Request parameters ===

    /// <summary>Start of the requested chart window in Unix milliseconds.</summary>
    public long StartTime { get; init; }

    /// <summary>End of the requested chart window in Unix milliseconds.</summary>
    public long EndTime { get; init; }

    /// <summary>IOB/COB time-series resolution in minutes.</summary>
    public int IntervalMinutes { get; init; }

    /// <summary>StartTime minus 8 hours, used for buffered IOB/COB fetches.</summary>
    public long BufferStartTime { get; init; }

    // === Profile-derived config (set by ProfileLoadStage) ===

    /// <summary>IANA timezone string from the active profile, or null if no profile is loaded.</summary>
    public string? Timezone { get; init; }

    /// <summary>Glucose threshold values derived from the active profile.</summary>
    public ChartThresholdsDto Thresholds { get; init; } = new();

    /// <summary>Default scheduled basal rate from the active profile (U/hr).</summary>
    public double DefaultBasalRate { get; init; } = 1.0;

    // === Raw fetched data (set by DataFetchStage) ===

    public IReadOnlyList<SensorGlucose> SensorGlucoseList { get; init; } = [];
    public IReadOnlyList<Bolus> BolusList { get; init; } = [];

    /// <summary>Boluses filtered to the display window (StartTime..EndTime).</summary>
    public IReadOnlyList<Bolus> DisplayBoluses { get; init; } = [];

    public IReadOnlyList<CarbIntake> CarbIntakeList { get; init; } = [];

    /// <summary>Carb intakes filtered to the display window (StartTime..EndTime).</summary>
    public IReadOnlyList<CarbIntake> DisplayCarbIntakes { get; init; } = [];

    public IReadOnlyList<BGCheck> BgCheckList { get; init; } = [];
    public IReadOnlyList<DeviceEvent> DeviceEventList { get; init; } = [];
    public IReadOnlyList<TempBasal> TempBasalList { get; init; } = [];
    /// <summary>State spans keyed by category, populated from a batched repository query.</summary>
    public IReadOnlyDictionary<StateSpanCategory, IEnumerable<StateSpan>> StateSpans { get; init; }
        = new Dictionary<StateSpanCategory, IEnumerable<StateSpan>>();

    public IReadOnlyList<SystemEvent> SystemEvents { get; init; } = [];
    public IReadOnlyList<TrackerDefinitionEntity> TrackerDefinitions { get; init; } = [];
    public IReadOnlyList<TrackerInstanceEntity> TrackerInstances { get; init; } = [];

    // === Intermediate computed data (set by TreatmentAdapterStage) ===

    /// <summary>Synthetic Treatment adapter objects built from v4 Bolus and CarbIntake records.</summary>
    public IReadOnlyList<Treatment> SyntheticTreatments { get; init; } = [];

    /// <summary>TreatmentFood records grouped by their parent CarbIntake ID.</summary>
    public IReadOnlyDictionary<Guid, List<TreatmentFood>> FoodsByCarbIntake { get; init; }
        = new Dictionary<Guid, List<TreatmentFood>>();

    // === Computed series (set by computation stages) ===

    public List<TimeSeriesPoint> IobSeries { get; init; } = [];
    public List<TimeSeriesPoint> CobSeries { get; init; } = [];
    public double MaxIob { get; init; }
    public double MaxCob { get; init; }
    public List<BasalPoint> BasalSeries { get; init; } = [];
    public double MaxBasalRate { get; init; }
    public List<GlucosePointDto> GlucoseData { get; init; } = [];
    public double GlucoseYMax { get; init; }

    // === Markers (set by DtoMappingStage) ===

    public List<BolusMarkerDto> BolusMarkers { get; init; } = [];
    public List<CarbMarkerDto> CarbMarkers { get; init; } = [];
    public List<BgCheckMarkerDto> BgCheckMarkers { get; init; } = [];
    public List<DeviceEventMarkerDto> DeviceEventMarkers { get; init; } = [];
    public List<SystemEventMarkerDto> SystemEventMarkers { get; init; } = [];
    public List<TrackerMarkerDto> TrackerMarkers { get; init; } = [];

    // === Spans (set by DtoMappingStage) ===

    public List<ChartStateSpanDto> PumpModeSpans { get; init; } = [];
    public List<ChartStateSpanDto> ProfileSpans { get; init; } = [];
    public List<ChartStateSpanDto> OverrideSpans { get; init; } = [];
    public List<ChartStateSpanDto> ActivitySpans { get; init; } = [];
    public List<ChartStateSpanDto> TempBasalSpans { get; init; } = [];
    public List<BasalDeliverySpanDto> BasalDeliverySpans { get; init; } = [];
}
