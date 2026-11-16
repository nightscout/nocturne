using System.Globalization;

namespace Nocturne.API.Reports;

/// <summary>
/// One figure the view prints under a panel, label above value: its words, its value as the API
/// wrote it, its unit (printed muted, apart from the value) and an optional short note under it.
/// </summary>
/// <param name="About">
/// The fields the figure is about (<see cref="ReportAbout"/>): a chart layer prints it only when it draws
/// every one of them. Null for a figure only a table or tiles panel prints.
/// </param>
internal sealed record ReportFact(string Label, string Value, string? Unit = null, string? Note = null,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<string>? About = null)
{
    public ReportFact Of(params string[] fields) => this with { About = fields };

    /// <summary>A figure that sets the data against the profile's settings: a layer that draws both may print it, a panel's headline never does.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ComparesSettings { get; init; }

    public ReportFact AgainstSettings() => this with { ComparesSettings = true };
}

/// <summary>
/// What a fact or a sentence is about, as a list of fields a layer must draw all of. One entry may name
/// fields that carry the same values in other units, split by '|' ("glucose|glucoseMgdl"): drawing any of
/// them draws that entry.
/// </summary>
internal static class ReportAbout
{
    public static bool DrawnBy(IReadOnlyList<string> about, IReadOnlyCollection<string> drawn) =>
        about.Count > 0 && about.All(entry => entry.Split('|').Any(drawn.Contains));
}

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
    /// charts' time axes are drawn in, written as <paramref name="clock"/> writes dates and times;
    /// "... UTC" when the tenant has none.
    /// </summary>
    public static string Window(DateTimeOffset from, DateTimeOffset to, ReportClock? clock = null)
    {
        clock ??= ReportClock.Utc;
        return $"{clock.DateTime(from)} to {clock.DateTime(to)} {clock.ZoneName}";
    }

    /// <summary>"2026-10-26 14:05 Australia/Sydney": one instant in the tenant's zone, "... UTC" when it has none.</summary>
    public static string Instant(DateTimeOffset at, ReportClock? clock = null)
    {
        clock ??= ReportClock.Utc;
        return $"{clock.DateTime(at)} {clock.ZoneName}";
    }

    /// <summary>"1 reading" or "4025 readings".</summary>
    public static string Plural(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? noun : noun + "s")}");
}

/// <summary>
/// How a report writes the dates and times in its sentences, facts and subtitles: in the tenant's zone, in the order
/// the reader's region writes a date (with English month names, as the sentences are English) and with the reader's
/// clock. A reader with no region gets ISO dates, and one with no clock a 24-hour clock.
/// </summary>
/// <param name="Region">A region format tag, as the display preferences hold it (<c>en-US</c>, <c>de-DE</c>).</param>
internal sealed record ReportClock(TimeZoneInfo? Zone = null, bool? Hour12 = null, string? Region = null)
{
    public static readonly ReportClock Utc = new();

    public static implicit operator ReportClock(TimeZoneInfo? zone) => new(zone);

    /// <summary>The zone's IANA name, or UTC.</summary>
    public string ZoneName => Zone is null ? "UTC"
        : TimeZoneInfo.TryConvertWindowsIdToIanaId(Zone.Id, out var iana) ? iana : Zone.Id;

    private string DatePattern => Order(Region) switch
    {
        'd' => "d MMM yyyy",
        'M' => "MMM d, yyyy",
        _ => "yyyy-MM-dd",
    };

    private string TimePattern => Hour12 == true ? "h:mm tt" : "HH:mm";

    /// <summary>"2 Nov 2026", "Nov 2, 2026" or "2026-11-02".</summary>
    public string Date(DateOnly day) => day.ToString(DatePattern, CultureInfo.InvariantCulture);

    /// <summary>The date and time of <paramref name="at"/> in the zone: "2 Nov 2026 11:48", "Nov 2, 2026 11:48 AM".</summary>
    public string DateTime(DateTimeOffset at)
    {
        var local = Zone is null ? at.UtcDateTime : TimeZoneInfo.ConvertTime(at, Zone).DateTime;
        return $"{Date(DateOnly.FromDateTime(local))} {local.ToString(TimePattern, CultureInfo.InvariantCulture)}";
    }

    /// <summary>Which of day, month or year the region writes first; null for no region, or one .NET does not know.</summary>
    private static char? Order(string? region)
    {
        if (string.IsNullOrWhiteSpace(region))
            return null;
        try
        {
            var pattern = CultureInfo.GetCultureInfo(region, predefinedOnly: true).DateTimeFormat.ShortDatePattern;
            return pattern.FirstOrDefault(c => c is 'd' or 'M' or 'y') is var first and not '\0' ? first : null;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}
