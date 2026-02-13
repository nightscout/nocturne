using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Nocturne.Core.Models.Attributes;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents an entry for glucose readings, similar to the legacy sgv collection
/// </summary>
public class Entry : ProcessableDocumentBase
{
    /// <summary>
    /// Gets or sets the MongoDB ObjectId
    /// </summary>
    [JsonPropertyName("_id")]
    public override string? Id { get; set; }

    /// <summary>
    /// Gets or sets the time in milliseconds since the Unix epoch
    /// If not set but DateString is available, will be calculated from DateString
    /// Nightscout returns both "date" and "mills" on GET requests (same value).
    /// </summary>
    [JsonPropertyName("mills")]
    public override long Mills
    {
        get
        {
            if (_mills != 0)
                return _mills;

            // If mills is not set but date is available, calculate it
            if (_date.HasValue)
            {
                return ((DateTimeOffset)DateTime.SpecifyKind(_date.Value, DateTimeKind.Utc))
                    .ToUnixTimeMilliseconds();
            }

            // If mills is not set but dateString is available, calculate it
            if (!string.IsNullOrEmpty(_dateString))
            {
                if (
                    DateTime.TryParse(
                        _dateString,
                        null,
                        System.Globalization.DateTimeStyles.RoundtripKind,
                        out var parsedDate
                    )
                )
                {
                    return (
                        (DateTimeOffset)DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc)
                    ).ToUnixTimeMilliseconds();
                }
            }

            return _mills;
        }
        set => _mills = value;
    }
    private long _mills;

    /// <summary>
    /// Gets or sets the date and time when this glucose reading was taken.
    /// If not set but Mills or DateString is available, will be calculated.
    /// </summary>
    [JsonIgnore]
    public DateTime? Date
    {
        get
        {
            // If date is not set but we have mills, calculate it
            if (_date == null && _mills > 0)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(_mills).UtcDateTime;
            }
            // If date is not set but we have dateString, parse it
            if (_date == null && !string.IsNullOrEmpty(_dateString))
            {
                if (
                    DateTime.TryParse(
                        _dateString,
                        null,
                        System.Globalization.DateTimeStyles.RoundtripKind,
                        out var parsedDate
                    )
                )
                {
                    return DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc);
                }
            }
            return _date;
        }
        set => _date = value;
    }
    private DateTime? _date;

    /// <summary>
    /// Gets or sets the date and time as an ISO 8601 string
    /// If not set but Date or Mills is available, will be calculated
    /// </summary>
    [JsonPropertyName("dateString")]
    public string? DateString
    {
        get
        {
            // If dateString is not set but we have date, format it
            if (string.IsNullOrEmpty(_dateString) && _date.HasValue)
            {
                return _date.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            }
            // If dateString is not set but we have mills, format it
            if (string.IsNullOrEmpty(_dateString) && _mills > 0)
            {
                return DateTimeOffset
                    .FromUnixTimeMilliseconds(_mills)
                    .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            }
            return _dateString;
        }
        set => _dateString = value;
    }
    private string? _dateString;

    /// <summary>
    /// Gets or sets the glucose value in mg/dL
    /// </summary>
    [JsonPropertyName("mgdl")]
    public double Mgdl { get; set; }

    /// <summary>
    /// Gets or sets the meter blood glucose in mg/dL
    /// </summary>
    [JsonPropertyName("mbg")]
    public double? Mbg { get; set; }

    /// <summary>
    /// Gets or sets the glucose value in mmol/L
    /// </summary>
    [JsonPropertyName("mmol")]
    public double? Mmol { get; set; }

    /// <summary>
    /// Gets or sets the sensor glucose value in mg/dL
    /// </summary>
    [JsonPropertyName("sgv")]
    public double? Sgv { get; set; }

    /// <summary>
    /// Gets or sets the direction of glucose trend as string for legacy compatibility
    /// Common values: Flat, SingleUp, DoubleUp, SingleDown, DoubleDown, FortyFiveUp, FortyFiveDown
    /// </summary>
    [JsonPropertyName("direction")]
    public string? Direction { get; set; }

    /// <summary>
    /// Gets or sets the numeric trend indicator (1-9) used by Dexcom and Loop
    /// 1=DoubleUp, 2=SingleUp, 3=FortyFiveUp, 4=Flat, 5=FortyFiveDown, 6=SingleDown, 7=DoubleDown, 8=NotComputable, 9=RateOutOfRange
    /// </summary>
    [JsonPropertyName("trend")]
    public int? Trend { get; set; }

    /// <summary>
    /// Gets or sets the rate of glucose change in mg/dL per minute
    /// Positive values indicate rising glucose, negative values indicate falling
    /// </summary>
    [JsonPropertyName("trendRate")]
    public double? TrendRate { get; set; }

    /// <summary>
    /// Gets or sets whether this entry is a calibration reading
    /// </summary>
    [JsonPropertyName("isCalibration")]
    public bool IsCalibration { get; set; }

    /// <summary>
    /// Gets the direction as a strongly-typed enum value
    /// </summary>
    [JsonIgnore]
    public Models.Direction DirectionEnum => ParseDirection(Direction);

    /// <summary>
    /// Parse direction string to enum - handles legacy string values
    /// </summary>
    private static Models.Direction ParseDirection(string? directionString)
    {
        if (string.IsNullOrEmpty(directionString))
            return Models.Direction.NONE;

        return directionString switch
        {
            "NONE" => Models.Direction.NONE,
            "TripleUp" => Models.Direction.TripleUp,
            "DoubleUp" => Models.Direction.DoubleUp,
            "SingleUp" => Models.Direction.SingleUp,
            "FortyFiveUp" => Models.Direction.FortyFiveUp,
            "Flat" => Models.Direction.Flat,
            "FortyFiveDown" => Models.Direction.FortyFiveDown,
            "SingleDown" => Models.Direction.SingleDown,
            "DoubleDown" => Models.Direction.DoubleDown,
            "TripleDown" => Models.Direction.TripleDown,
            "NOT COMPUTABLE" => Models.Direction.NotComputable,
            "RATE OUT OF RANGE" => Models.Direction.RateOutOfRange,
            "CGM ERROR" => Models.Direction.CgmError,
            _ => Models.Direction.NONE,
        };
    }

    /// <summary>
    /// Gets or sets the entry type (e.g., "sgv", "cal", "mbg")
    /// </summary>
    [JsonPropertyName("type")]
    [Sanitizable]
    public string Type { get; set; } = "sgv";

    /// <summary>
    /// Gets or sets the device identifier
    /// </summary>
    [JsonPropertyName("device")]
    [Sanitizable]
    public string? Device { get; set; }

    /// <summary>
    /// Gets or sets any additional notes or comments
    /// </summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// Gets or sets the delta (change) from the previous reading
    /// </summary>
    [JsonPropertyName("delta")]
    public double? Delta { get; set; }

    /// <summary>
    /// Gets or sets the scaled glucose value
    /// </summary>
    [JsonPropertyName("scaled")]
    public object? Scaled { get; set; }

    /// <summary>
    /// Gets or sets the system time when the entry was processed.
    /// </summary>
    [JsonPropertyName("sysTime")]
    public string? SysTime { get; set; }

    /// <summary>
    /// Gets or sets UTC offset information
    /// </summary>
    [JsonPropertyName("utcOffset")]
    public override int? UtcOffset { get; set; }

    /// <summary>
    /// Gets or sets the noise level (0-4)
    /// </summary>
    [JsonPropertyName("noise")]
    public int? Noise { get; set; }

    /// <summary>
    /// Gets or sets whether this entry has been filtered.
    /// Nightscout only includes this field when it has a value.
    /// </summary>
    [JsonPropertyName("filtered")]
    public double? Filtered { get; set; }

    /// <summary>
    /// Gets or sets the unfiltered value.
    /// Nightscout only includes this field when it has a value.
    /// </summary>
    [JsonPropertyName("unfiltered")]
    public double? Unfiltered { get; set; }

    /// <summary>
    /// Gets or sets the RSSI signal strength
    /// </summary>
    [JsonPropertyName("rssi")]
    public int? Rssi { get; set; }

    /// <summary>
    /// Gets or sets the slope value
    /// </summary>
    [JsonPropertyName("slope")]
    public double? Slope { get; set; }

    /// <summary>
    /// Gets or sets the intercept value
    /// </summary>
    [JsonPropertyName("intercept")]
    public double? Intercept { get; set; }

    /// <summary>
    /// Gets or sets the scale value
    /// </summary>
    [JsonPropertyName("scale")]
    public double? Scale { get; set; }

    /// <summary>
    /// Gets or sets when this entry was created
    /// </summary>
    [JsonPropertyName("created_at")]
    public override string? CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets when this entry was last modified
    /// </summary>
    [JsonPropertyName("modified_at")]
    public DateTime? ModifiedAt { get; set; }

    /// <summary>
    /// Gets or sets the data source identifier indicating where this entry originated from.
    /// Use constants from <see cref="Core.Constants.DataSources"/> for consistent values.
    /// </summary>
    /// <example>
    /// Common values: "demo-service", "dexcom-connector", "manual", "mongodb-import"
    /// </example>
    [JsonPropertyName("data_source")]
    [NocturneOnly]
    public string? DataSource { get; set; }

    /// <summary>
    /// Gets or sets additional metadata for the entry
    /// </summary>
    [JsonPropertyName("meta")]
    [NocturneOnly]
    public Dictionary<string, object>? Meta { get; set; }

    /// <summary>
    /// Gets or sets the canonical group ID for deduplication.
    /// Records with the same CanonicalId represent the same underlying event from different sources.
    /// </summary>
    [JsonPropertyName("canonicalId")]
    [NocturneOnly]
    public Guid? CanonicalId { get; set; }

    /// <summary>
    /// Gets or sets the list of data sources that contributed to this unified record.
    /// Only populated when returning merged/unified DTOs.
    /// </summary>
    [JsonPropertyName("sources")]
    [NocturneOnly]
    public string[]? Sources { get; set; }
}
