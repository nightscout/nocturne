namespace Nocturne.Core.Models;

/// <summary>
/// Complete dashboard chart data response.
/// Contains all pre-computed data needed to render the glucose chart in a single payload.
/// </summary>
public class DashboardChartData
{
    // === Time series ===
    public List<TimeSeriesPoint> IobSeries { get; set; } = new();
    public List<TimeSeriesPoint> CobSeries { get; set; } = new();
    public List<BasalPoint> BasalSeries { get; set; } = new();
    public double DefaultBasalRate { get; set; }
    public double MaxBasalRate { get; set; }
    public double MaxIob { get; set; }
    public double MaxCob { get; set; }

    // === Glucose data ===
    public List<GlucosePointDto> GlucoseData { get; set; } = new();
    public ChartThresholdsDto Thresholds { get; set; } = new();

    // === Treatment markers ===
    public List<BolusMarkerDto> BolusMarkers { get; set; } = new();
    public List<CarbMarkerDto> CarbMarkers { get; set; } = new();
    public List<DeviceEventMarkerDto> DeviceEventMarkers { get; set; } = new();

    // === State spans ===
    public List<ChartStateSpanDto> PumpModeSpans { get; set; } = new();
    public List<ChartStateSpanDto> ProfileSpans { get; set; } = new();
    public List<ChartStateSpanDto> OverrideSpans { get; set; } = new();
    public List<ChartStateSpanDto> ActivitySpans { get; set; } = new();
    public List<ChartStateSpanDto> TempBasalSpans { get; set; } = new();
    public List<BasalDeliverySpanDto> BasalDeliverySpans { get; set; } = new();

    // === System events ===
    public List<SystemEventMarkerDto> SystemEventMarkers { get; set; } = new();

    // === Tracker markers ===
    public List<TrackerMarkerDto> TrackerMarkers { get; set; } = new();
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

public class TimeSeriesPoint
{
    public long Timestamp { get; set; }
    public double Value { get; set; }
}

public class BasalPoint
{
    public long Timestamp { get; set; }
    public double Rate { get; set; }
    public double ScheduledRate { get; set; }
    public BasalDeliveryOrigin Origin { get; set; }
    public ChartColor FillColor { get; set; }
    public ChartColor StrokeColor { get; set; }
}

public class GlucosePointDto
{
    public long Time { get; set; }
    public double Sgv { get; set; }
    public string? Direction { get; set; }
}

public class BolusMarkerDto
{
    public long Time { get; set; }
    public double Insulin { get; set; }
    public string? TreatmentId { get; set; }
    public BolusType BolusType { get; set; }
    public bool IsOverride { get; set; }
}

public class CarbMarkerDto
{
    public long Time { get; set; }
    public double Carbs { get; set; }
    public string? Label { get; set; }
    public string? TreatmentId { get; set; }
    public bool IsOffset { get; set; }
}

public class DeviceEventMarkerDto
{
    public long Time { get; set; }
    public DeviceEventType EventType { get; set; }
    public string? Notes { get; set; }
    public ChartColor Color { get; set; }
}

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
