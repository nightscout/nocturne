using Nocturne.Core.Models.V4;

namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Request body for upserting a CGM sensor glucose reading via the V4 API.
/// </summary>
/// <seealso cref="Validators.V4.UpsertSensorGlucoseRequestValidator"/>
/// <seealso cref="Nocturne.API.Controllers.V4.Glucose.SensorGlucoseController"/>
public class UpsertSensorGlucoseRequest
{
    /// <summary>
    /// When the sensor reading was taken.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes at the time of the event, for local-time display.
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Identifier of the CGM transmitter or receiver.
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Name of the application that submitted this record.
    /// </summary>
    public string? App { get; set; }

    /// <summary>
    /// Upstream data source identifier.
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// Glucose reading in mg/dL (validated 0-10,000).
    /// </summary>
    public double Mgdl { get; set; }

    /// <summary>
    /// Glucose trend direction (rising, falling, stable, etc.).
    /// </summary>
    public GlucoseDirection? Direction { get; set; }

    /// <summary>
    /// Rate of glucose change in mg/dL per minute.
    /// </summary>
    public double? TrendRate { get; set; }

    /// <summary>
    /// Sensor noise level indicator (device-specific scale).
    /// </summary>
    public int? Noise { get; set; }

    /// <summary>
    /// Raw filtered sensor value (scaled ADC)
    /// </summary>
    public double? Filtered { get; set; }

    /// <summary>
    /// Raw unfiltered sensor value (scaled ADC)
    /// </summary>
    public double? Unfiltered { get; set; }

    /// <summary>
    /// Glucose delta in mg/dL over the last 5 minutes
    /// </summary>
    public double? Delta { get; set; }

    /// <summary>
    /// Whether this glucose value is smoothed or unsmoothed.
    /// Accepted values: "Smoothed", "Unsmoothed". Case-insensitive. Null for unknown.
    /// </summary>
    public string? GlucoseProcessing { get; set; }

    /// <summary>
    /// Smoothed glucose value in mg/dL, when known.
    /// </summary>
    public double? SmoothedMgdl { get; set; }

    /// <summary>
    /// Unsmoothed (raw) glucose value in mg/dL, when known.
    /// </summary>
    public double? UnsmoothedMgdl { get; set; }
}
