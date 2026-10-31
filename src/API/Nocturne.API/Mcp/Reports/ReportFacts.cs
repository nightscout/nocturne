using System.Globalization;

namespace Nocturne.API.Mcp.Reports;

/// <summary>
/// One figure the view prints under a panel, label above value: its words, its value as the API
/// wrote it, its unit (printed muted, apart from the value) and an optional short note under it.
/// </summary>
internal sealed record ReportFact(string Label, string Value, string? Unit = null, string? Note = null);

/// <summary>Builds a panel's <see cref="ReportFact"/>s in the display unit, invariant culture.</summary>
internal static class ReportFacts
{
    /// <summary>The most facts a panel carries; the summary carries the rest.</summary>
    public const int Max = 6;

    public static string Number(double value, string format = "0.#") => value.ToString(format, CultureInfo.InvariantCulture);

    public static ReportFact Percent(string label, double value, string? note = null) => new(label, Number(value), "%", note);

    public static ReportFact Glucose(string label, double mgdl, bool useMmol, string? note = null) =>
        new(label, GlucoseSummary.Format(mgdl, useMmol), GlucoseUnit(useMmol), note);

    public static ReportFact Count(string label, int count, string? unit = null, string? note = null) =>
        new(label, count.ToString(CultureInfo.InvariantCulture), unit, note);

    public static string GlucoseUnit(bool useMmol) => useMmol ? "mmol/L" : "mg/dL";

    /// <summary>
    /// "2026-10-15 19:39 to 2026-10-29 19:39 Australia/Sydney" in the tenant's zone, the zone the
    /// charts' time axes are drawn in; "... UTC" when the tenant has none.
    /// </summary>
    public static string Window(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo? zone = null)
    {
        if (zone is null)
            return string.Create(CultureInfo.InvariantCulture, $"{from.UtcDateTime:yyyy-MM-dd HH:mm} to {to.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
        var name = TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;
        return string.Create(CultureInfo.InvariantCulture,
            $"{TimeZoneInfo.ConvertTime(from, zone):yyyy-MM-dd HH:mm} to {TimeZoneInfo.ConvertTime(to, zone):yyyy-MM-dd HH:mm} {name}");
    }

    /// <summary>"1 reading" or "4025 readings".</summary>
    public static string Plural(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? noun : noun + "s")}");
}
