using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Configuration;

/// <summary>
/// Per-user display preferences that follow the user across devices and tenants.
/// Stored as a JSONB blob on the subject and served via the user preferences endpoint.
/// Every property is nullable so a PATCH carries only the fields being changed and the
/// server merges them over the stored value. String values mirror the frontend's literal
/// unions (e.g. units "mg/dl"/"mmol") so the generated client stays a single shared shape.
/// Language is intentionally excluded: it lives in its own subject column because the
/// session and server-side locale resolution depend on it.
/// </summary>
public class UserDisplayPreferences
{
    /// <summary>Shared JSON options for (de)serializing the preferences blob (camelCase / web defaults).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Deserializes a stored preferences blob, returning an empty (all-null) instance when the
    /// input is null/blank or cannot be parsed. Never throws — a corrupt blob degrades to defaults.
    /// </summary>
    public static UserDisplayPreferences Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new UserDisplayPreferences();
        }

        try
        {
            return JsonSerializer.Deserialize<UserDisplayPreferences>(json, JsonOptions)
                ?? new UserDisplayPreferences();
        }
        catch (JsonException)
        {
            return new UserDisplayPreferences();
        }
    }

    /// <summary>Serializes this instance to the JSONB storage representation.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    // Allowed values for the constrained string preferences (mirror the frontend literal unions).
    private static readonly HashSet<string> AllowedGlucoseUnits = new(StringComparer.Ordinal) { "mg/dl", "mmol" };
    private static readonly HashSet<string> AllowedTimeFormats = new(StringComparer.Ordinal) { "12", "24" };

    /// <summary>
    /// Regional formats offered to the user. Empty string means "follow the display language".
    /// Each tag drives date ordering, month/weekday names and the first day of the week through
    /// Intl on the frontend, so adding one here is all that a new region needs.
    /// </summary>
    private static readonly HashSet<string> AllowedRegionFormats = new(StringComparer.Ordinal)
    {
        "",
        "en-US", "en-GB", "en-AU", "en-CA", "en-IE", "en-NZ", "en-ZA",
        "de-DE", "fr-FR", "es-ES", "it-IT", "nl-NL", "pl-PL", "pt-PT", "pt-BR",
        "sv-SE", "nb-NO", "da-DK", "fi-FI", "cs-CZ", "ru-RU", "ja-JP",
    };

    private static readonly HashSet<string> AllowedColorThemes = new(StringComparer.Ordinal) { "nocturne", "trio", "aaps", "classic" };
    private static readonly HashSet<string> AllowedSidebarWidgets = new(StringComparer.Ordinal) { "graph", "halo-dial" };
    private static readonly HashSet<string> AllowedPredictionModes = new(StringComparer.Ordinal) { "cone", "lines", "main", "iob", "zt", "uam", "cob" };
    private static readonly HashSet<string> AllowedColorModes = new(StringComparer.Ordinal) { "single", "threshold", "continuous" };
    private static readonly HashSet<string> AllowedAreaModes = new(StringComparer.Ordinal) { "off", "baseline", "deviation" };

    /// <summary>
    /// Validates the constrained string values and numeric ranges. Returns a "field: message"
    /// description of the first invalid value, or null when every present value is acceptable.
    /// Null fields are skipped (a partial payload only validates what it carries).
    /// </summary>
    public string? Validate()
    {
        var stringError =
            Check("glucoseUnits", GlucoseUnits, AllowedGlucoseUnits)
            ?? Check("timeFormat", TimeFormat, AllowedTimeFormats)
            ?? Check("regionFormat", RegionFormat, AllowedRegionFormats)
            ?? Check("colorTheme", ColorTheme, AllowedColorThemes)
            ?? Check("sidebarWidget", SidebarWidget, AllowedSidebarWidgets)
            ?? Check("prediction.displayMode", Prediction?.DisplayMode, AllowedPredictionModes)
            ?? Check("chart.lineColorMode", Chart?.LineColorMode, AllowedColorModes)
            ?? Check("chart.pointColorMode", Chart?.PointColorMode, AllowedColorModes)
            ?? Check("chart.areaMode", Chart?.AreaMode, AllowedAreaModes);
        if (stringError != null)
        {
            return stringError;
        }

        if (Chart?.AreaOpacity is { } opacity && (opacity < 0 || opacity > 1))
        {
            return "chart.areaOpacity: must be between 0 and 1";
        }
        if (Prediction?.Minutes is { } minutes && minutes < 0)
        {
            return "prediction.minutes: must be non-negative";
        }
        if (Chart?.Lookback is { } lookback && lookback <= 0)
        {
            return "chart.lookback: must be greater than 0";
        }

        return null;

        static string? Check(string field, string? value, HashSet<string> allowed) =>
            value != null && !allowed.Contains(value)
                ? $"{field}: '{value}' is not allowed. Valid values: {string.Join(", ", allowed)}"
                : null;
    }

    /// <summary>
    /// Merges the non-null fields of <paramref name="incoming"/> into this instance so unset
    /// fields are preserved. Nested prediction/chart objects merge field-by-field; scalar and
    /// list values replace wholesale when present.
    /// </summary>
    public void MergeWith(UserDisplayPreferences incoming)
    {
        GlucoseUnits = incoming.GlucoseUnits ?? GlucoseUnits;
        TimeFormat = incoming.TimeFormat ?? TimeFormat;
        RegionFormat = incoming.RegionFormat ?? RegionFormat;
        ColorTheme = incoming.ColorTheme ?? ColorTheme;
        NightModeSchedule = incoming.NightModeSchedule ?? NightModeSchedule;
        DashboardTopWidgets = incoming.DashboardTopWidgets ?? DashboardTopWidgets;
        SidebarWidget = incoming.SidebarWidget ?? SidebarWidget;

        if (incoming.Prediction is { } prediction)
        {
            Prediction ??= new PredictionPreferences();
            Prediction.Enabled = prediction.Enabled ?? Prediction.Enabled;
            Prediction.Minutes = prediction.Minutes ?? Prediction.Minutes;
            Prediction.DisplayMode = prediction.DisplayMode ?? Prediction.DisplayMode;
        }

        if (incoming.Chart is { } chart)
        {
            Chart ??= new ChartPreferences();
            Chart.LineColorMode = chart.LineColorMode ?? Chart.LineColorMode;
            Chart.LineColor = chart.LineColor ?? Chart.LineColor;
            Chart.PointColorMode = chart.PointColorMode ?? Chart.PointColorMode;
            Chart.PointColor = chart.PointColor ?? Chart.PointColor;
            Chart.ShowPoints = chart.ShowPoints ?? Chart.ShowPoints;
            Chart.AreaMode = chart.AreaMode ?? Chart.AreaMode;
            Chart.AreaOpacity = chart.AreaOpacity ?? Chart.AreaOpacity;
            Chart.AlwaysShowPatterns = chart.AlwaysShowPatterns ?? Chart.AlwaysShowPatterns;
            Chart.Lookback = chart.Lookback ?? Chart.Lookback;
        }
    }

    /// <summary>Glucose units: "mg/dl" or "mmol".</summary>
    [JsonPropertyName("glucoseUnits")]
    public string? GlucoseUnits { get; set; }

    /// <summary>Time format: "12" or "24".</summary>
    [JsonPropertyName("timeFormat")]
    public string? TimeFormat { get; set; }

    /// <summary>
    /// Regional format as a BCP-47 tag (e.g. "en-GB"), driving date ordering, month and weekday
    /// names, and the first day of the week. Empty string follows the display language.
    /// Separate from language so a user can read the UI in English on a European calendar.
    /// </summary>
    [JsonPropertyName("regionFormat")]
    public string? RegionFormat { get; set; }

    /// <summary>Color theme: "nocturne", "trio", "aaps", or "classic".</summary>
    [JsonPropertyName("colorTheme")]
    public string? ColorTheme { get; set; }

    /// <summary>Whether the automatic night-mode schedule is enabled.</summary>
    [JsonPropertyName("nightModeSchedule")]
    public bool? NightModeSchedule { get; set; }

    /// <summary>Prediction display preferences.</summary>
    [JsonPropertyName("prediction")]
    public PredictionPreferences? Prediction { get; set; }

    /// <summary>Glucose-chart visual style preferences.</summary>
    [JsonPropertyName("chart")]
    public ChartPreferences? Chart { get; set; }

    /// <summary>Ordered widget IDs shown in the dashboard top-widget grid.</summary>
    [JsonPropertyName("dashboardTopWidgets")]
    public List<WidgetId>? DashboardTopWidgets { get; set; }

    /// <summary>Sidebar widget preference: "graph" or "halo-dial".</summary>
    [JsonPropertyName("sidebarWidget")]
    public string? SidebarWidget { get; set; }
}

/// <summary>
/// Prediction display preferences (mirrors the frontend prediction settings).
/// </summary>
public class PredictionPreferences
{
    /// <summary>Whether prediction lines are shown on charts.</summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    /// <summary>Prediction time horizon in minutes.</summary>
    [JsonPropertyName("minutes")]
    public int? Minutes { get; set; }

    /// <summary>Prediction display mode: "cone", "lines", "main", "iob", "zt", "uam", or "cob".</summary>
    [JsonPropertyName("displayMode")]
    public string? DisplayMode { get; set; }
}

/// <summary>
/// Glucose-chart visual style preferences (mirrors the frontend chart settings).
/// </summary>
public class ChartPreferences
{
    /// <summary>Line color mode: "single", "threshold", or "continuous".</summary>
    [JsonPropertyName("lineColorMode")]
    public string? LineColorMode { get; set; }

    /// <summary>Line color as a hex string (used when line color mode is "single").</summary>
    [JsonPropertyName("lineColor")]
    public string? LineColor { get; set; }

    /// <summary>Point color mode: "single", "threshold", or "continuous".</summary>
    [JsonPropertyName("pointColorMode")]
    public string? PointColorMode { get; set; }

    /// <summary>Point color as a hex string (used when point color mode is "single").</summary>
    [JsonPropertyName("pointColor")]
    public string? PointColor { get; set; }

    /// <summary>Whether individual glucose points are rendered.</summary>
    [JsonPropertyName("showPoints")]
    public bool? ShowPoints { get; set; }

    /// <summary>Area fill mode: "off", "baseline", or "deviation".</summary>
    [JsonPropertyName("areaMode")]
    public string? AreaMode { get; set; }

    /// <summary>Area fill opacity (0..1).</summary>
    [JsonPropertyName("areaOpacity")]
    public double? AreaOpacity { get; set; }

    /// <summary>Always render range/category patterns on screen (accessibility aid).</summary>
    [JsonPropertyName("alwaysShowPatterns")]
    public bool? AlwaysShowPatterns { get; set; }

    /// <summary>Glucose-chart lookback window width in hours (may be a custom fractional value).</summary>
    [JsonPropertyName("lookback")]
    public double? Lookback { get; set; }
}
