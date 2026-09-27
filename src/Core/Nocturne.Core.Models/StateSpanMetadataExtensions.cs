using System.Globalization;
using System.Text.Json;

namespace Nocturne.Core.Models;

/// <summary>
/// Helpers for reading typed values from <see cref="StateSpan.Metadata"/>, which round-trips
/// through JSONB and may surface as boxed CLR values, <see cref="JsonElement"/>, or strings
/// depending on which path populated the dictionary.
/// </summary>
/// <remarks>
/// Centralised so any consumer of <c>StateSpan.Metadata</c> reads it consistently. The
/// alternative — each call site writing its own switch — drifts: one site forgets
/// <c>JsonElement</c>, another forgets non-finite-double guarding, and metadata reads
/// silently disagree across the codebase.
/// </remarks>
public static class StateSpanMetadataExtensions
{
    /// <summary>
    /// Metadata key naming the Nightscout collection an <see cref="StateSpanCategory.Override"/>
    /// span was decomposed from: <see cref="TreatmentsCollection"/> or <see cref="DeviceStatusCollection"/>.
    /// </summary>
    public const string CollectionKey = "collection";

    /// <summary>A "Temporary Override" treatment.</summary>
    public const string TreatmentsCollection = "treatments";

    /// <summary>A devicestatus <c>override</c>.</summary>
    public const string DeviceStatusCollection = "devicestatus";

    /// <summary>
    /// Reads <paramref name="key"/> as a <see cref="decimal"/>; returns <see langword="null"/>
    /// if missing, non-finite, or unparseable. Numeric strings are parsed with
    /// <see cref="CultureInfo.InvariantCulture"/>.
    /// </summary>
    public static decimal? TryReadDecimal(this IDictionary<string, object>? metadata, string key)
    {
        if (metadata is null) return null;
        if (!metadata.TryGetValue(key, out var v) || v is null) return null;
        return CoerceDecimal(v);
    }

    /// <summary>
    /// Reads <paramref name="key"/> as a <see cref="string"/>; returns <see langword="null"/>
    /// if missing or not a string-typed value. No coercion: a numeric metadata value will
    /// not be ToString'd here.
    /// </summary>
    public static string? TryReadString(this IDictionary<string, object>? metadata, string key)
    {
        if (metadata is null) return null;
        if (!metadata.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => null,
        };
    }

    /// <summary>
    /// Whether two <see cref="StateSpanCategory.Override"/> spans are Loop's two records of one
    /// override, a "Temporary Override" treatment and a devicestatus snapshot, so neither ends the
    /// other: by name when either has one, else by scale factor. Two records of one collection never
    /// match, nor does a record without <see cref="CollectionKey"/>.
    /// </summary>
    public static bool IsSameOverrideAs(
        this IDictionary<string, object>? metadata, IDictionary<string, object>? other)
    {
        if (metadata is null || other is null || !AreTreatmentAndDeviceStatus(metadata, other))
            return false;

        var names = OverrideNames(metadata);
        var otherNames = OverrideNames(other);
        if (names.Count == 0 && otherNames.Count == 0)
            return metadata.OverrideScaleFactor() == other.OverrideScaleFactor();

        return names.Overlaps(otherNames);
    }

    /// <summary>
    /// Devicestatus <c>multiplier</c> or treatment <c>insulinNeedsScaleFactor</c>. Loop omits it at 100%.
    /// </summary>
    public static decimal OverrideScaleFactor(this IDictionary<string, object> metadata) =>
        metadata.TryReadDecimal("multiplier") ?? metadata.TryReadDecimal("insulinNeedsScaleFactor") ?? 1m;

    private static bool AreTreatmentAndDeviceStatus(
        IDictionary<string, object> metadata, IDictionary<string, object> other) =>
        (metadata.TryReadString(CollectionKey), other.TryReadString(CollectionKey)) is
            (TreatmentsCollection, DeviceStatusCollection) or (DeviceStatusCollection, TreatmentsCollection);

    private static HashSet<string> OverrideNames(IDictionary<string, object> metadata)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (metadata.ContainsKey("name"))
        {
            if (metadata.TryReadString("name") is { Length: > 0 } name)
                names.Add(name);
        }
        else if (metadata.TryReadString("reason") is { Length: > 0 } reason)
        {
            names.Add(reason);
            // Loop prefixes a treatment reason with the preset symbol, at most two characters.
            if (reason.IndexOf(' ') is var space and > 0
                && space + 1 < reason.Length
                && new StringInfo(reason[..space]).LengthInTextElements <= 2)
                names.Add(reason[(space + 1)..]);
        }

        return names;
    }

    private static decimal? CoerceDecimal(object v) => v switch
    {
        decimal d => d,
        double dbl when double.IsFinite(dbl) => (decimal)dbl,
        float f when float.IsFinite(f) => (decimal)f,
        int i => i,
        long l => l,
        string s when decimal.TryParse(s, CultureInfo.InvariantCulture, out var p) => p,
        JsonElement je => CoerceDecimal(je),
        _ => null,
    };

    private static decimal? CoerceDecimal(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.Number when je.TryGetDecimal(out var dec) => dec,
        JsonValueKind.String when decimal.TryParse(
            je.GetString(), CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };
}
