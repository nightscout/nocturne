using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for Entry (glucose readings)
/// Maps to Nocturne.Core.Models.Entry
/// </summary>
[Table("entries")]
public class EntryEntity
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Original MongoDB ObjectId as string for reference/migration tracking
    /// </summary>
    [Column("original_id")]
    [MaxLength(24)]
    public string? OriginalId { get; set; }

    /// <summary>
    /// Time in milliseconds since Unix epoch
    /// </summary>
    [Column("mills")]
    public long Mills { get; set; }

    /// <summary>
    /// Date and time as ISO 8601 string
    /// </summary>
    [Column("dateString")]
    [MaxLength(50)]
    public string? DateString { get; set; }

    /// <summary>
    /// Glucose value in mg/dL
    /// </summary>
    [Column("mgdl")]
    public double Mgdl { get; set; }

    /// <summary>
    /// Glucose value in mmol/L
    /// </summary>
    [Column("mmol")]
    public double? Mmol { get; set; }

    /// <summary>
    /// Sensor glucose value in mg/dL
    /// </summary>
    [Column("sgv")]
    public double? Sgv { get; set; }

    /// <summary>
    /// Direction of glucose trend
    /// </summary>
    [Column("direction")]
    [MaxLength(50)]
    public string? Direction { get; set; }

    /// <summary>
    /// Numeric trend indicator (1-9) used by Dexcom and Loop
    /// 1=DoubleUp, 2=SingleUp, 3=FortyFiveUp, 4=Flat, 5=FortyFiveDown, 6=SingleDown, 7=DoubleDown, 8=NotComputable, 9=RateOutOfRange
    /// </summary>
    [Column("trend")]
    public int? Trend { get; set; }

    /// <summary>
    /// Rate of glucose change in mg/dL per minute
    /// </summary>
    [Column("trend_rate")]
    public double? TrendRate { get; set; }

    /// <summary>
    /// Whether this entry is a calibration reading
    /// </summary>
    [Column("is_calibration")]
    public bool IsCalibration { get; set; }

    /// <summary>
    /// Entry type (sgv, mbg, cal, etc.)
    /// </summary>
    [Column("type")]
    [MaxLength(50)]
    public string Type { get; set; } = "sgv";

    /// <summary>
    /// Device identifier
    /// </summary>
    [Column("device")]
    [MaxLength(255)]
    public string? Device { get; set; }

    /// <summary>
    /// Notes or comments
    /// </summary>
    [Column("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// Delta (change) from previous reading
    /// </summary>
    [Column("delta")]
    public double? Delta { get; set; }

    /// <summary>
    /// Scaled glucose value (stored as JSON)
    /// </summary>
    [Column("scaled", TypeName = "jsonb")]
    public string? ScaledJson { get; set; }

    /// <summary>
    /// System time when entry was processed
    /// </summary>
    [Column("sysTime")]
    [MaxLength(50)]
    public string? SysTime { get; set; }

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    [Column("utcOffset")]
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Noise level (0-4)
    /// </summary>
    [Column("noise")]
    public int? Noise { get; set; }

    /// <summary>
    /// Filtered glucose value
    /// </summary>
    [Column("filtered")]
    public double? Filtered { get; set; }

    /// <summary>
    /// Unfiltered glucose value
    /// </summary>
    [Column("unfiltered")]
    public double? Unfiltered { get; set; }

    /// <summary>
    /// RSSI signal strength
    /// </summary>
    [Column("rssi")]
    public int? Rssi { get; set; }

    /// <summary>
    /// Slope value
    /// </summary>
    [Column("slope")]
    public double? Slope { get; set; }

    /// <summary>
    /// Intercept value
    /// </summary>
    [Column("intercept")]
    public double? Intercept { get; set; }

    /// <summary>
    /// Scale value
    /// </summary>
    [Column("scale")]
    public double? Scale { get; set; }

    /// <summary>
    /// When this entry was created
    /// </summary>
    [Column("created_at")]
    [MaxLength(50)]
    public string? CreatedAt { get; set; }

    /// <summary>
    /// When this entry was last modified
    /// </summary>
    [Column("modified_at")]
    public DateTime? ModifiedAt { get; set; }

    /// <summary>
    /// Data source identifier indicating where this entry originated from.
    /// Use constants from Nocturne.Core.Constants.DataSources for consistent values.
    /// Examples: "demo-service", "dexcom-connector", "manual", "mongodb-import"
    /// </summary>
    [Column("data_source")]
    [MaxLength(50)]
    public string? DataSource { get; set; }

    /// <summary>
    /// Additional metadata (stored as JSON)
    /// </summary>
    [Column("meta", TypeName = "jsonb")]
    public string? MetaJson { get; set; }

    /// <summary>
    /// Additional properties (stored as JSON)
    /// </summary>
    [Column("additional_properties", TypeName = "jsonb")]
    public string? AdditionalPropertiesJson { get; set; }

    // === AAPS (AndroidAPS) Fields ===

    /// <summary>
    /// Application identifier that created this entry (e.g., "AAPS", "xdrip")
    /// </summary>
    [Column("app")]
    [MaxLength(255)]
    public string? App { get; set; }

    /// <summary>
    /// Glucose measurement units (e.g., "mg/dl", "mmol/L")
    /// </summary>
    [Column("units")]
    [MaxLength(20)]
    public string? Units { get; set; }

    /// <summary>
    /// Whether the entry is valid. AAPS sets this to false for soft-deleted records.
    /// </summary>
    [Column("is_valid")]
    public bool? IsValid { get; set; }

    /// <summary>
    /// Whether the entry is read-only and should not be modified by the client
    /// </summary>
    [Column("is_read_only")]
    public bool? IsReadOnly { get; set; }

    /// <summary>
    /// System tracking: when record was inserted
    /// </summary>
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// System tracking: when record was last updated
    /// </summary>
    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Parses the Notes field if it contains valid JSON and extracts matching properties
    /// </summary>
    public void ParseNotesJson()
    {
        if (string.IsNullOrWhiteSpace(Notes))
            return;

        try
        {
            using var document = JsonDocument.Parse(Notes);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return;

            var additionalProperties = new Dictionary<string, object>();
            var hasExistingAdditional = !string.IsNullOrWhiteSpace(AdditionalPropertiesJson);

            if (hasExistingAdditional)
            {
                try
                {
                    var existingAdditional = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        AdditionalPropertiesJson!
                    );
                    if (existingAdditional != null)
                    {
                        foreach (var kvp in existingAdditional)
                            additionalProperties[kvp.Key] = kvp.Value;
                    }
                }
                catch
                { /* Ignore invalid existing additional properties */
                }
            }

            foreach (var property in root.EnumerateObject())
            {
                var propertyName = property.Name.ToLowerInvariant();
                var propertyValue = property.Value;

                // Try to match against existing entity properties
                switch (propertyName)
                {
                    case "mgdl" when propertyValue.ValueKind == JsonValueKind.Number:
                        if (propertyValue.TryGetDouble(out var mgdlValue))
                            Mgdl = mgdlValue;
                        break;
                    case "mmol" when propertyValue.ValueKind == JsonValueKind.Number:
                        if (propertyValue.TryGetDouble(out var mmolValue))
                            Mmol = mmolValue;
                        break;
                    case "sgv" when propertyValue.ValueKind == JsonValueKind.Number:
                        if (propertyValue.TryGetDouble(out var sgvValue))
                            Sgv = sgvValue;
                        break;
                    case "direction" when propertyValue.ValueKind == JsonValueKind.String:
                        Direction = propertyValue.GetString();
                        break;
                    case "delta" when propertyValue.ValueKind == JsonValueKind.Number:
                        if (propertyValue.TryGetDouble(out var deltaValue))
                            Delta = deltaValue;
                        break;
                    case "device" when propertyValue.ValueKind == JsonValueKind.String:
                        Device = propertyValue.GetString();
                        break;
                    case "noise" when propertyValue.ValueKind == JsonValueKind.Number:
                        if (propertyValue.TryGetInt32(out var noiseValue))
                            Noise = noiseValue;
                        break;
                    case "filtered" when propertyValue.ValueKind == JsonValueKind.Number:
                        if (propertyValue.TryGetDouble(out var filteredValue))
                            Filtered = filteredValue;
                        break;
                    case "unfiltered" when propertyValue.ValueKind == JsonValueKind.Number:
                        if (propertyValue.TryGetDouble(out var unfilteredValue))
                            Unfiltered = unfilteredValue;
                        break;
                    case "rssi" when propertyValue.ValueKind == JsonValueKind.Number:
                        if (propertyValue.TryGetInt32(out var rssiValue))
                            Rssi = rssiValue;
                        break;
                    case "slope" when propertyValue.ValueKind == JsonValueKind.Number:
                        if (propertyValue.TryGetDouble(out var slopeValue))
                            Slope = slopeValue;
                        break;
                    case "intercept" when propertyValue.ValueKind == JsonValueKind.Number:
                        if (propertyValue.TryGetDouble(out var interceptValue))
                            Intercept = interceptValue;
                        break;
                    case "scale" when propertyValue.ValueKind == JsonValueKind.Number:
                        if (propertyValue.TryGetDouble(out var scaleValue))
                            Scale = scaleValue;
                        break;
                    case "utcoffset" when propertyValue.ValueKind == JsonValueKind.Number:
                        if (propertyValue.TryGetInt32(out var utcOffsetValue))
                            UtcOffset = utcOffsetValue;
                        break;
                    default:
                        // Store unmatched properties in additional metadata
                        additionalProperties[property.Name] =
                            JsonSerializer.Deserialize<object>(propertyValue.GetRawText())
                            ?? new object();
                        break;
                }
            }

            // Update AdditionalPropertiesJson with additional properties
            if (additionalProperties.Count > 0)
            {
                AdditionalPropertiesJson = JsonSerializer.Serialize(
                    additionalProperties,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
                );
            }

            // Clear the Notes field since we've processed the JSON
            Notes = null;
        }
        catch (JsonException)
        {
            // Not valid JSON, leave Notes as-is
        }
    }
}
