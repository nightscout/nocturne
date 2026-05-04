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
