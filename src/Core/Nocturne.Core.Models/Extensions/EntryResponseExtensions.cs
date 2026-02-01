using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Extensions;

/// <summary>
/// Extension methods for converting Entry domain models to API response formats.
/// These methods handle the computed properties required for Nightscout V1 and V3 API compatibility.
/// </summary>
public static class EntryResponseExtensions
{
    /// <summary>
    /// Converts an Entry to V1 API response format.
    /// V1 returns: _id, date, mills, dateString, sysTime, and all data properties.
    /// </summary>
    public static object ToV1Response(this Entry entry)
    {
        return new EntryV1Response(entry);
    }

    /// <summary>
    /// Converts an Entry to V3 API response format.
    /// V3 adds: identifier, srvModified, srvCreated (all computed from core fields).
    /// </summary>
    public static object ToV3Response(this Entry entry)
    {
        return new EntryV3Response(entry);
    }

    /// <summary>
    /// Converts multiple entries to V1 response format.
    /// </summary>
    public static IEnumerable<object> ToV1Responses(this IEnumerable<Entry> entries)
    {
        return entries.Select(e => e.ToV1Response());
    }

    /// <summary>
    /// Converts multiple entries to V3 response format.
    /// </summary>
    public static IEnumerable<object> ToV3Responses(this IEnumerable<Entry> entries)
    {
        return entries.Select(e => e.ToV3Response());
    }

    /// <summary>
    /// Formats Unix milliseconds to ISO 8601 date string.
    /// </summary>
    internal static string FormatDateString(long mills)
    {
        if (mills <= 0)
            return string.Empty;

        return DateTimeOffset.FromUnixTimeMilliseconds(mills).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    }
}

/// <summary>
/// V1 API response format for Entry.
/// Includes all Nightscout V1 compatible fields.
/// </summary>
public class EntryV1Response
{
    private readonly Entry _entry;

    public EntryV1Response(Entry entry)
    {
        _entry = entry;
    }

    [JsonPropertyName("_id")]
    public string? Id => _entry.Id;

    [JsonPropertyName("date")]
    public long Date => _entry.Mills;

    [JsonPropertyName("mills")]
    public long Mills => _entry.Mills;

    [JsonPropertyName("dateString")]
    public string? DateString =>
        !string.IsNullOrEmpty(_entry.DateString)
            ? _entry.DateString
            : EntryResponseExtensions.FormatDateString(_entry.Mills);

    [JsonPropertyName("sysTime")]
    public string? SysTime => _entry.SysTime ?? DateString;

    [JsonPropertyName("mgdl")]
    public double Mgdl => _entry.Mgdl;

    [JsonPropertyName("sgv")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Sgv => _entry.Sgv;

    [JsonPropertyName("mbg")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Mbg => _entry.Mbg;

    [JsonPropertyName("mmol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Mmol => _entry.Mmol;

    [JsonPropertyName("direction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Direction => _entry.Direction;

    [JsonPropertyName("trend")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Trend => _entry.Trend;

    [JsonPropertyName("trendRate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TrendRate => _entry.TrendRate;

    [JsonPropertyName("isCalibration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsCalibration => _entry.IsCalibration;

    [JsonPropertyName("type")]
    public string Type => _entry.Type;

    [JsonPropertyName("device")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Device => _entry.Device;

    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes => _entry.Notes;

    [JsonPropertyName("delta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Delta => _entry.Delta;

    [JsonPropertyName("scaled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Scaled => _entry.Scaled;

    [JsonPropertyName("utcOffset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? UtcOffset => _entry.UtcOffset;

    [JsonPropertyName("noise")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Noise => _entry.Noise;

    [JsonPropertyName("filtered")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Filtered => _entry.Filtered;

    [JsonPropertyName("unfiltered")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Unfiltered => _entry.Unfiltered;

    [JsonPropertyName("rssi")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Rssi => _entry.Rssi;

    [JsonPropertyName("slope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Slope => _entry.Slope;

    [JsonPropertyName("intercept")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Intercept => _entry.Intercept;

    [JsonPropertyName("scale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Scale => _entry.Scale;

    [JsonPropertyName("created_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedAt => _entry.CreatedAt;
}

/// <summary>
/// V3 API response format for Entry.
/// Extends V1 format with identifier, srvModified, and srvCreated fields.
/// </summary>
public class EntryV3Response
{
    private readonly Entry _entry;

    public EntryV3Response(Entry entry)
    {
        _entry = entry;
    }

    // V3-specific computed fields
    [JsonPropertyName("identifier")]
    public string? Identifier => _entry.Id;

    [JsonPropertyName("srvModified")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SrvModified => _entry.Mills > 0 ? _entry.Mills : null;

    [JsonPropertyName("srvCreated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SrvCreated => _entry.Mills > 0 ? _entry.Mills : null;

    // Core fields (same as V1)
    [JsonPropertyName("_id")]
    public string? Id => _entry.Id;

    [JsonPropertyName("date")]
    public long Date => _entry.Mills;

    [JsonPropertyName("mills")]
    public long Mills => _entry.Mills;

    [JsonPropertyName("dateString")]
    public string? DateString =>
        !string.IsNullOrEmpty(_entry.DateString)
            ? _entry.DateString
            : EntryResponseExtensions.FormatDateString(_entry.Mills);

    [JsonPropertyName("sysTime")]
    public string? SysTime => _entry.SysTime ?? DateString;

    [JsonPropertyName("mgdl")]
    public double Mgdl => _entry.Mgdl;

    [JsonPropertyName("sgv")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Sgv => _entry.Sgv;

    [JsonPropertyName("mbg")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Mbg => _entry.Mbg;

    [JsonPropertyName("mmol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Mmol => _entry.Mmol;

    [JsonPropertyName("direction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Direction => _entry.Direction;

    [JsonPropertyName("trend")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Trend => _entry.Trend;

    [JsonPropertyName("trendRate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TrendRate => _entry.TrendRate;

    [JsonPropertyName("isCalibration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsCalibration => _entry.IsCalibration;

    [JsonPropertyName("type")]
    public string Type => _entry.Type;

    [JsonPropertyName("device")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Device => _entry.Device;

    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes => _entry.Notes;

    [JsonPropertyName("delta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Delta => _entry.Delta;

    [JsonPropertyName("scaled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Scaled => _entry.Scaled;

    [JsonPropertyName("utcOffset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? UtcOffset => _entry.UtcOffset;

    [JsonPropertyName("noise")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Noise => _entry.Noise;

    [JsonPropertyName("filtered")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Filtered => _entry.Filtered;

    [JsonPropertyName("unfiltered")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Unfiltered => _entry.Unfiltered;

    [JsonPropertyName("rssi")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Rssi => _entry.Rssi;

    [JsonPropertyName("slope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Slope => _entry.Slope;

    [JsonPropertyName("intercept")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Intercept => _entry.Intercept;

    [JsonPropertyName("scale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Scale => _entry.Scale;

    [JsonPropertyName("created_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedAt => _entry.CreatedAt;
}
