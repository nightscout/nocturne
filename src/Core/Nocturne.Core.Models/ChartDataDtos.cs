using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Complete dashboard chart data response.
/// Contains all pre-computed data needed to render the glucose chart in a single payload.
/// </summary>
/// <remarks>
/// All data is server-computed following the "backend is source of truth" principle.
/// Colors are assigned as <see cref="ChartColor"/> values that map to CSS custom properties on the frontend.
/// </remarks>
/// <seealso cref="Entry"/>
/// <seealso cref="Treatment"/>
/// <seealso cref="StateSpan"/>
/// <seealso cref="SystemEvent"/>
public class DashboardChartData
{
    // === Time series ===

    /// <summary>Insulin on Board time series (timestamp + IOB value).</summary>
    public List<TimeSeriesPoint> IobSeries { get; set; } = new();

    /// <summary>Carbs on Board time series (timestamp + COB value).</summary>
    public List<TimeSeriesPoint> CobSeries { get; set; } = new();

    /// <summary>Basal rate time series with scheduled rate and delivery origin.</summary>
    public List<BasalPoint> BasalSeries { get; set; } = new();

    /// <summary>Default basal rate from the active profile (U/hr).</summary>
    public double DefaultBasalRate { get; set; }

    /// <summary>Maximum basal rate in the time window, used for Y-axis scaling.</summary>
    public double MaxBasalRate { get; set; }

    /// <summary>Maximum IOB value in the time window, used for Y-axis scaling.</summary>
    public double MaxIob { get; set; }

    /// <summary>Maximum COB value in the time window, used for Y-axis scaling.</summary>
    public double MaxCob { get; set; }

    // === Glucose data ===

    /// <summary>Glucose readings from <see cref="Entry"/> records.</summary>
    public List<GlucosePointDto> GlucoseData { get; set; } = new();

    /// <summary>Glucose threshold configuration derived from the active <see cref="Profile"/>.</summary>
    public ChartThresholdsDto Thresholds { get; set; } = new();

    // === Treatment markers ===

    /// <summary>Bolus insulin markers from <see cref="Treatment"/> records.</summary>
    public List<BolusMarkerDto> BolusMarkers { get; set; } = new();

    /// <summary>Carbohydrate intake markers from <see cref="Treatment"/> records.</summary>
    public List<CarbMarkerDto> CarbMarkers { get; set; } = new();

    /// <summary>Device event markers (site changes, sensor starts) from <see cref="Treatment"/> records.</summary>
    public List<DeviceEventMarkerDto> DeviceEventMarkers { get; set; } = new();

    /// <summary>Blood glucose check markers from <see cref="Treatment"/> records.</summary>
    public List<BgCheckMarkerDto> BgCheckMarkers { get; set; } = new();

    // === State spans ===

    /// <summary>Pump mode spans from <see cref="StateSpan"/> records with <see cref="StateSpanCategory.PumpMode"/>.</summary>
    public List<ChartStateSpanDto> PumpModeSpans { get; set; } = new();

    /// <summary>Profile spans from <see cref="StateSpan"/> records with <see cref="StateSpanCategory.Profile"/>.</summary>
    public List<ChartStateSpanDto> ProfileSpans { get; set; } = new();

    /// <summary>Override spans from <see cref="StateSpan"/> records with <see cref="StateSpanCategory.Override"/>.</summary>
    public List<ChartStateSpanDto> OverrideSpans { get; set; } = new();

    /// <summary>Activity spans (sleep, exercise, illness, travel) from <see cref="StateSpan"/> records.</summary>
    public List<ChartStateSpanDto> ActivitySpans { get; set; } = new();

    /// <summary>Temporary basal spans from legacy <see cref="Treatment"/> temp basal records.</summary>
    public List<ChartStateSpanDto> TempBasalSpans { get; set; } = new();

    /// <summary>Basal delivery spans with rate and origin information.</summary>
    public List<BasalDeliverySpanDto> BasalDeliverySpans { get; set; } = new();

    // === System events ===

    /// <summary>System event markers (alarms, warnings) from <see cref="SystemEvent"/> records.</summary>
    public List<SystemEventMarkerDto> SystemEventMarkers { get; set; } = new();

    // === Tracker markers ===

    /// <summary>Tracker markers (consumable ages, appointments) for chart overlay.</summary>
    public List<TrackerMarkerDto> TrackerMarkers { get; set; } = new();

    // === Health data ===

    /// <summary>Heart rate time series for background chart overlay.</summary>
    public List<HeartRatePointDto> HeartRateSeries { get; set; } = new();

    /// <summary>Step count series for background chart bubble overlay.</summary>
    public List<StepBubbleDto> StepSeries { get; set; } = new();
}

/// <summary>
/// Glucose threshold configuration derived from the active profile.
/// </summary>
public record ChartThresholdsDto
{
    public double Low { get; init; }
    public double High { get; init; }
    public double VeryLow { get; init; }
    public double VeryHigh { get; init; }
    public double GlucoseYMax { get; init; }
}

/// <summary>
/// A single data point in a time series (IOB, COB).
/// </summary>
public class TimeSeriesPoint
{
    public long Timestamp { get; set; }
    public double Value { get; set; }
}

/// <summary>
/// A basal rate data point with scheduled rate comparison and delivery origin.
/// </summary>
/// <seealso cref="BasalDeliveryOrigin"/>
/// <seealso cref="ChartColor"/>
public class BasalPoint
{
    public long Timestamp { get; set; }
    public double Rate { get; set; }
    public double ScheduledRate { get; set; }
    public BasalDeliveryOrigin Origin { get; set; }
    public ChartColor FillColor { get; set; }
    public ChartColor StrokeColor { get; set; }
}

/// <summary>
/// Glucose data point projected from an <see cref="Entry"/> for chart rendering.
/// </summary>
public class GlucosePointDto
{
    public long Time { get; set; }
    public double Sgv { get; set; }
    public string? Direction { get; set; }
    public string? DataSource { get; set; }
}

/// <summary>
/// Bolus marker projected from a <see cref="Treatment"/> for chart rendering.
/// </summary>
/// <seealso cref="BolusType"/>
public class BolusMarkerDto
{
    public long Time { get; set; }
    public double Insulin { get; set; }
    public string? TreatmentId { get; set; }
    public BolusType BolusType { get; set; }
    public bool IsOverride { get; set; }
    public string? DataSource { get; set; }
}

/// <summary>
/// Carbohydrate marker projected from a <see cref="Treatment"/> for chart rendering.
/// </summary>
public class CarbMarkerDto
{
    public long Time { get; set; }
    public double Carbs { get; set; }
    public string? Label { get; set; }
    public string? TreatmentId { get; set; }
    public bool IsOffset { get; set; }
    public string? DataSource { get; set; }
}

/// <summary>
/// Device event marker (site change, sensor start, etc.) for chart rendering.
/// </summary>
/// <seealso cref="DeviceEventType"/>
/// <seealso cref="ChartColor"/>
public class DeviceEventMarkerDto
{
    public long Time { get; set; }
    public DeviceEventType EventType { get; set; }
    public string? Notes { get; set; }
    public string? TreatmentId { get; set; }
    public ChartColor Color { get; set; }
}

/// <summary>
/// Blood glucose check marker for chart rendering. Sourced from <see cref="Treatment"/> BG Check events.
/// </summary>
public class BgCheckMarkerDto
{
    public long Time { get; set; }
    public double Glucose { get; set; }
    public string? GlucoseType { get; set; }
    public string? TreatmentId { get; set; }
}

/// <summary>
/// System event marker for chart rendering. Sourced from <see cref="SystemEvent"/> records.
/// </summary>
/// <seealso cref="SystemEventType"/>
/// <seealso cref="SystemEventCategory"/>
/// <seealso cref="ChartColor"/>
public class SystemEventMarkerDto
{
    public string Id { get; set; } = "";
    public long Time { get; set; }
    public SystemEventType EventType { get; set; }
    public SystemEventCategory Category { get; set; }
    public string? Code { get; set; }
    public string? Description { get; set; }
    public ChartColor Color { get; set; }
}

/// <summary>
/// State span DTO for chart rendering. Projected from <see cref="StateSpan"/> records.
/// </summary>
/// <seealso cref="StateSpanCategory"/>
/// <seealso cref="ChartColor"/>
public class ChartStateSpanDto
{
    public string Id { get; set; } = "";
    public StateSpanCategory Category { get; set; }
    public string State { get; set; } = "";
    public long StartMills { get; set; }
    public long? EndMills { get; set; }
    public ChartColor Color { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>
/// Basal delivery span with rate and origin for chart rendering.
/// </summary>
/// <seealso cref="BasalDeliveryOrigin"/>
/// <seealso cref="ChartColor"/>
public class BasalDeliverySpanDto
{
    public string Id { get; set; } = "";
    public long StartMills { get; set; }
    public long? EndMills { get; set; }
    public double Rate { get; set; }
    public BasalDeliveryOrigin Origin { get; set; }
    public string? Source { get; set; }
    public ChartColor FillColor { get; set; }
    public ChartColor StrokeColor { get; set; }
}

/// <summary>
/// Tracker marker (consumable age, appointment) for chart rendering.
/// </summary>
/// <seealso cref="TrackerCategory"/>
/// <seealso cref="ChartColor"/>
public class TrackerMarkerDto
{
    public string Id { get; set; } = "";
    public string DefinitionId { get; set; } = "";
    public string Name { get; set; } = "";
    public TrackerCategory Category { get; set; }
    public long Time { get; set; }
    public string? Icon { get; set; }
    public ChartColor Color { get; set; }
}

/// <summary>
/// Heart rate data point for chart rendering.
/// </summary>
public class HeartRatePointDto
{
    public long Time { get; set; }
    public int Bpm { get; set; }
}

/// <summary>
/// Step count data point for chart rendering as a sized bubble.
/// </summary>
public class StepBubbleDto
{
    public long Time { get; set; }
    public int Steps { get; set; }
}

/// <summary>
/// What initiated the basal delivery rate.
/// Used by the chart system to color and categorize basal delivery spans.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<BasalDeliveryOrigin>))]
public enum BasalDeliveryOrigin
{
    /// <summary>
    /// Closed-loop algorithm adjusted (CamAPS, Control-IQ, Loop)
    /// </summary>
    Algorithm,

    /// <summary>
    /// Pump's programmed basal schedule
    /// </summary>
    Scheduled,

    /// <summary>
    /// User-initiated temporary rate
    /// </summary>
    Manual,

    /// <summary>
    /// Delivery suspended (rate = 0)
    /// </summary>
    Suspended,

    /// <summary>
    /// Derived from profile when no pump data available
    /// </summary>
    Inferred
}
