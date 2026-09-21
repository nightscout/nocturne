using System.Text.Json;

namespace Nocturne.Connectors.Glooko.Xt;

/// <summary>
///     What a device-synced record's <c>memo</c> carries. A vendor sync (CamAPS, and presumably
///     the others) writes a JSON object there — <c>{"source":"CamAPS Sync","detail":{...}}</c> —
///     on every record it uploads, while a patient typing in the app leaves free text. The two
///     must not be confused: the blob is provenance, not a logbook entry, and its <c>detail</c> is
///     where a pump alarm or a bolus-wizard calculation lives.
/// </summary>
public sealed class GlookoXtProvenance
{
    /// <summary>The uploading integration, e.g. <c>CamAPS Sync</c>.</summary>
    public string? Source { get; init; }

    /// <summary>Record kind the integration tagged, e.g. <c>wizard</c>.</summary>
    public string? Type { get; init; }

    /// <summary>Pump alarm code, e.g. <c>alarm_occlusion</c>.</summary>
    public string? RawAlarm { get; init; }

    /// <summary>Bolus-wizard figures when <see cref="Type"/> is <c>wizard</c>.</summary>
    public GlookoXtWizardDetail? Wizard { get; init; }

    /// <summary>Whether the source is a closed-loop system, whose basal stream is algorithm-driven.</summary>
    public bool IsClosedLoop =>
        Source is not null && Source.Contains("camaps", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Parses <paramref name="memo"/>; null when it is not a provenance blob, in which case the
    ///     memo is the patient's own text.
    /// </summary>
    public static GlookoXtProvenance? TryParse(string? memo)
    {
        if (string.IsNullOrWhiteSpace(memo)) return null;
        var trimmed = memo.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return null;

        try
        {
            using var doc = JsonDocument.Parse(memo);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("source", out var source))
                return null;

            JsonElement? detail = root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.Object ? d : null;
            var type = Str(root, "type");

            return new GlookoXtProvenance
            {
                Source = source.ValueKind == JsonValueKind.String ? source.GetString() : null,
                Type = type,
                RawAlarm = detail is { } dd ? Str(dd, "raw_alarm") : null,
                Wizard = string.Equals(type, "wizard", StringComparison.OrdinalIgnoreCase) ? ParseWizard(root, detail) : null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static GlookoXtWizardDetail ParseWizard(JsonElement root, JsonElement? detail)
    {
        JsonElement? recommended = root.TryGetProperty("recommended", out var r) && r.ValueKind == JsonValueKind.Object ? r : null;
        return new GlookoXtWizardDetail
        {
            Total = detail is { } d ? Num(d, "total_value") : null,
            BgInput = Num(root, "bgInput"),
            CarbInput = Num(root, "carbInput"),
            RecommendedCarb = recommended is { } rc ? Num(rc, "carb") : null,
            RecommendedNet = recommended is { } rn ? Num(rn, "net") : null,
            RecommendedCorrection = recommended is { } rr ? Num(rr, "correction") : null,
            Overridden = detail is { } od && string.Equals(Str(od, "suggestion_overridden"), "yes", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var p) => p,
            _ => null,
        };
    }
}

public sealed class GlookoXtWizardDetail
{
    public double? Total { get; init; }
    public double? BgInput { get; init; }
    public double? CarbInput { get; init; }
    public double? RecommendedCarb { get; init; }
    public double? RecommendedNet { get; init; }
    public double? RecommendedCorrection { get; init; }
    public bool Overridden { get; init; }
}
