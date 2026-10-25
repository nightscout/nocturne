using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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
    /// <summary>The display language of a subject who has not chosen one; the frontend's default language.</summary>
    public const string DefaultLanguage = "en";

    /// <summary>
    /// JSON options for (de)serializing the stored preferences blob (camelCase / web defaults).
    /// <see cref="EffectiveEnergyUnit"/> is left out: it is resolved on every response, and a stored
    /// copy would go stale when the region format changes. Enum lists read leniently (see
    /// <see cref="LenientEnumListConverterFactory"/>), so a blob a newer build wrote still loads.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OmitResolvedValues } },
        Converters = { new LenientEnumListConverterFactory() },
    };

    private static void OmitResolvedValues(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(UserDisplayPreferences))
            return;

        var resolved = typeInfo.Properties.Single(p => p.Name == "effectiveEnergyUnit");
        typeInfo.Properties.Remove(resolved);
    }

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
    internal static readonly HashSet<string> AllowedGlucoseUnits = new(StringComparer.Ordinal) { "mg/dl", "mmol" };
    internal static readonly HashSet<string> AllowedTimeFormats = new(StringComparer.Ordinal) { "12", "24" };
    private static readonly HashSet<string> AllowedA1cNames = new(StringComparer.Ordinal) { "HbA1c", "A1c" };
    private static readonly HashSet<string> AllowedA1cUnits = new(StringComparer.Ordinal) { "percent", "mmol/mol" };
    private static readonly HashSet<string> AllowedCarbRatioFormats = new(StringComparer.Ordinal) { "1:x", "g/U", "U/10g" };

    /// <summary>
    /// Regional formats offered to the user. Empty string means "follow the display language".
    /// Each tag drives date ordering, month/weekday names and the first day of the week through
    /// Intl on the frontend, so adding one here is all that a new region needs.
    /// </summary>
    internal static readonly HashSet<string> AllowedRegionFormats = new(StringComparer.Ordinal)
    {
        "",
        "en-US", "en-GB", "en-AU", "en-CA", "en-IE", "en-NZ", "en-ZA",
        "de-DE", "fr-FR", "es-ES", "it-IT", "nl-NL", "pl-PL", "pt-PT", "pt-BR",
        "sv-SE", "nb-NO", "da-DK", "fi-FI", "cs-CZ", "ru-RU", "ja-JP",
    };

    private static readonly HashSet<string> AllowedColorThemes = new(StringComparer.Ordinal) { "nocturne", "trio", "aaps", "classic" };
    private static readonly HashSet<string> AllowedGlucoseRangePalettes = new(StringComparer.Ordinal) { "theme", "clarity" };
    private static readonly HashSet<string> AllowedPredictionModes = new(StringComparer.Ordinal) { "cone", "lines", "main", "iob", "zt", "uam", "cob" };
    private static readonly HashSet<string> AllowedColorModes = new(StringComparer.Ordinal) { "single", "threshold", "continuous" };
    private static readonly HashSet<string> AllowedAreaModes = new(StringComparer.Ordinal) { "off", "full", "baseline", "deviation" };

    /// <summary>
    /// Validates the constrained string values and numeric ranges. Returns a "field: message"
    /// description of the first invalid value, or null when every present value is acceptable.
    /// Null fields are skipped (a partial payload only validates what it carries).
    /// </summary>
    public string? Validate()
    {
        var stringError =
            Check("glucoseUnits", GlucoseUnits, AllowedGlucoseUnits)
            ?? Check("a1cName", A1cName, AllowedA1cNames)
            ?? Check("a1cUnits", A1cUnits, AllowedA1cUnits)
            ?? Check("carbRatioFormat", CarbRatioFormat, AllowedCarbRatioFormats)
            ?? Check("timeFormat", TimeFormat, AllowedTimeFormats)
            ?? Check("regionFormat", RegionFormat, AllowedRegionFormats)
            ?? Check("energyUnit", EnergyUnit, EnergyUnits.Allowed)
            ?? Check("colorTheme", ColorTheme, AllowedColorThemes)
            ?? Check("glucoseRangePalette", GlucoseRangePalette, AllowedGlucoseRangePalettes)
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

        return CheckEnumList("dashboardTopWidgets", DashboardTopWidgets)
            ?? CheckEnumList("dashboardRail.order", DashboardRail?.Order)
            ?? CheckEnumList("dashboardRail.hidden", DashboardRail?.Hidden)
            ?? CheckEnumList("hiddenLegacyFallbacks", HiddenLegacyFallbacks)
            ?? YearOverviewColors?.Validate();
    }

    internal static string? CheckEnumList<TEnum>(string field, List<TEnum>? values)
        where TEnum : struct, Enum
    {
        if (values == null)
        {
            return null;
        }

        var defined = Enum.GetValues<TEnum>();
        if (values.Count > defined.Length)
        {
            return $"{field}: must not hold more than {defined.Length} values";
        }
        foreach (var value in values)
        {
            if (!Enum.IsDefined(value))
            {
                return $"{field}: '{value}' is not allowed. Valid values: {string.Join(", ", Enum.GetNames<TEnum>())}";
            }
        }
        if (values.Distinct().Count() != values.Count)
        {
            return $"{field}: must not repeat a value";
        }
        return null;
    }

    internal static string? Check(string field, string? value, HashSet<string> allowed) =>
        value != null && !allowed.Contains(value)
            ? $"{field}: '{value}' is not allowed. Valid values: {string.Join(", ", allowed)}"
            : null;

    /// <summary>
    /// Merges the non-null fields of <paramref name="incoming"/> into this instance so unset
    /// fields are preserved. Nested prediction/chart objects merge field-by-field; scalar and
    /// list values replace wholesale when present.
    /// </summary>
    public void MergeWith(UserDisplayPreferences incoming)
    {
        GlucoseUnits = incoming.GlucoseUnits ?? GlucoseUnits;
        A1cName = incoming.A1cName ?? A1cName;
        A1cUnits = incoming.A1cUnits ?? A1cUnits;
        CarbRatioFormat = incoming.CarbRatioFormat ?? CarbRatioFormat;
        TimeFormat = incoming.TimeFormat ?? TimeFormat;
        RegionFormat = incoming.RegionFormat ?? RegionFormat;
        EnergyUnit = incoming.EnergyUnit ?? EnergyUnit;
        ColorTheme = incoming.ColorTheme ?? ColorTheme;
        GlucoseRangePalette = incoming.GlucoseRangePalette ?? GlucoseRangePalette;
        NightModeSchedule = incoming.NightModeSchedule ?? NightModeSchedule;
        CalendarShowTirPercent = incoming.CalendarShowTirPercent ?? CalendarShowTirPercent;
        DashboardTopWidgets = incoming.DashboardTopWidgets ?? DashboardTopWidgets;
        // Replace the whole arrangement: an empty object resets the rail to its default.
        DashboardRail = incoming.DashboardRail ?? DashboardRail;
        // Replace the whole list: an empty list shows every legacy age again.
        HiddenLegacyFallbacks = incoming.HiddenLegacyFallbacks ?? HiddenLegacyFallbacks;
        // Replace the whole color selection: an empty object resets every metric to automatic.
        YearOverviewColors = incoming.YearOverviewColors ?? YearOverviewColors;

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

    /// <summary>
    /// A copy carrying only the fields that describe how data is drawn, for disclosure to a
    /// viewer who is not the owner (the anonymous public share). Each top-level field is named
    /// individually, so one added later is withheld until someone decides it is presentation
    /// rather than a fact about the owner. <see cref="Prediction"/> and <see cref="Chart"/> are
    /// carried whole and carry no such guarantee: a field added inside either is disclosed on
    /// the next build. Their contents are pinned by a test instead.
    /// <para>
    /// <see cref="DashboardTopWidgets"/>, <see cref="DashboardRail"/> and
    /// <see cref="HiddenLegacyFallbacks"/> are withheld: the widgets and status pills an owner
    /// picks say what they treat and track.
    /// <see cref="NightModeSchedule"/> is withheld: it reports when the owner sleeps, and when a
    /// viewer's own screen dims is the viewer's setting.
    /// </para>
    /// </summary>
    public UserDisplayPreferences ToPresentationOnly() => new()
    {
        GlucoseUnits = GlucoseUnits,
        A1cName = A1cName,
        A1cUnits = A1cUnits,
        CarbRatioFormat = CarbRatioFormat,
        TimeFormat = TimeFormat,
        RegionFormat = RegionFormat,
        EnergyUnit = EnergyUnit,
        ColorTheme = ColorTheme,
        GlucoseRangePalette = GlucoseRangePalette,
        CalendarShowTirPercent = CalendarShowTirPercent,
        Prediction = Prediction,
        Chart = Chart,
        YearOverviewColors = YearOverviewColors,
    };

    /// <summary>Glucose units: "mg/dl" or "mmol".</summary>
    [JsonPropertyName("glucoseUnits")]
    public string? GlucoseUnits { get; set; }

    public string? A1cName { get; set; }
    public string? A1cUnits { get; set; }

    /// <summary>
    /// How insulin-to-carb ratios are written: "1:x" (grams covered by one unit), "g/U", or "U/10g"
    /// (units for ten grams). Ratios are stored and served in grams per unit; this only changes the
    /// display. Null means "1:x".
    /// </summary>
    [JsonPropertyName("carbRatioFormat")]
    public string? CarbRatioFormat { get; set; }

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

    /// <summary>Food energy display unit: "kJ" or "kcal". Null follows <see cref="EffectiveEnergyUnit"/>.</summary>
    [JsonPropertyName("energyUnit")]
    public string? EnergyUnit { get; set; }

    /// <summary>
    /// The energy unit to display, resolved by <see cref="EnergyUnits.Resolve"/>. Served so the
    /// frontend never applies the region rule itself; never stored (see <see cref="JsonOptions"/>).
    /// </summary>
    [JsonPropertyName("effectiveEnergyUnit")]
    public string EffectiveEnergyUnit => EnergyUnits.Resolve(EnergyUnit, RegionFormat);

    /// <summary>Color theme: "nocturne", "trio", "aaps", or "classic".</summary>
    [JsonPropertyName("colorTheme")]
    public string? ColorTheme { get; set; }

    /// <summary>
    /// Glucose range palette: "theme" (the colour theme's own range colours) or "clarity"
    /// (distinct colours for each of the four out-of-range bands). Null means "theme".
    /// </summary>
    [JsonPropertyName("glucoseRangePalette")]
    public string? GlucoseRangePalette { get; set; }

    /// <summary>Whether the automatic night-mode schedule is enabled.</summary>
    [JsonPropertyName("nightModeSchedule")]
    public bool? NightModeSchedule { get; set; }

    /// <summary>Whether each calendar day prints its in-range percentage over its time-in-range bar.</summary>
    [JsonPropertyName("calendarShowTirPercent")]
    public bool? CalendarShowTirPercent { get; set; }

    [JsonPropertyName("prediction")]
    public PredictionPreferences? Prediction { get; set; }

    /// <summary>Glucose-chart visual style preferences.</summary>
    [JsonPropertyName("chart")]
    public ChartPreferences? Chart { get; set; }

    [JsonPropertyName("yearOverviewColors")]
    public YearOverviewColorPreferences? YearOverviewColors { get; set; }

    /// <summary>Ordered widget IDs shown in the dashboard top-widget grid.</summary>
    [JsonPropertyName("dashboardTopWidgets")]
    public List<WidgetId>? DashboardTopWidgets { get; set; }

    /// <summary>
    /// How the dashboard status rail beside the current glucose is arranged. Null or an empty
    /// object is the default rail.
    /// </summary>
    [JsonPropertyName("dashboardRail")]
    public DashboardRailPreference? DashboardRail { get; set; }

    /// <summary>
    /// The legacy consumable ages the user hid from the status rail's tracker pills. Null or empty
    /// hides none.
    /// </summary>
    [JsonPropertyName("hiddenLegacyFallbacks")]
    public List<LegacyConsumableKind>? HiddenLegacyFallbacks { get; set; }
}

/// <summary>
/// The user's arrangement of the dashboard status rail. Items not named in <see cref="Order"/>
/// follow it in their default order, so an item added in a later build still shows.
/// </summary>
public class DashboardRailPreference
{
    /// <summary>The items to show first, in this order.</summary>
    [JsonPropertyName("order")]
    public List<RailItemId>? Order { get; set; }

    /// <summary>The items not to show.</summary>
    [JsonPropertyName("hidden")]
    public List<RailItemId>? Hidden { get; set; }
}

/// <summary>An item the dashboard status rail can show.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RailItemId
{
    Cob,
    Basal,
    Iob,
    Loop,
    Reservoir,
    Trackers,
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

    /// <summary>Area fill mode: "off", "full", "baseline", or "deviation".</summary>
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
