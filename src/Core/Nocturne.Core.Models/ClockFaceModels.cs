using System.Text.Json.Serialization;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.Core.Models;

/// <summary>
/// Domain model for a saved clock face
/// </summary>
public class ClockFace
{
    /// <summary>
    /// Unique identifier - UUID v7, serves as unguessable public URL
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// User ID who owns this clock face
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// User-friendly name for the clock face
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Clock face configuration (rows, elements, settings)
    /// </summary>
    public ClockFaceConfig Config { get; set; } = new();

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}

public class ClockFaceConfig
{
    /// <summary>
    /// Rows of elements (implicit row breaks based on element grouping)
    /// </summary>
    [JsonPropertyName("rows")]
    public List<ClockRow> Rows { get; set; } = [];

    [JsonPropertyName("settings")]
    public ClockSettings Settings { get; set; } = new();

    /// <summary>
    /// Returns a "field: message" description of the first invalid setting, or null when the
    /// config can be saved.
    /// </summary>
    public string? Validate() =>
        (Settings is null ? "settings: must be an object" : Settings.Validate())
        ?? Rows.SelectMany(r => r.Elements).Select(e => e.Validate()).FirstOrDefault(error => error is not null);

    /// <summary>
    /// The layout every new face starts from: glucose and trend arrow, then delta, then reading
    /// age. It takes the creator's units and time format, which the face then keeps whoever
    /// views it; a preference the creator never set, or one outside the allowed set, leaves the
    /// <see cref="ClockSettings"/> default.
    /// </summary>
    public static ClockFaceConfig Starter(UserDisplayPreferences creator)
    {
        var settings = new ClockSettings();
        if (creator.GlucoseUnits is { } units && UserDisplayPreferences.AllowedGlucoseUnits.Contains(units))
        {
            settings.GlucoseUnits = units;
        }
        if (creator.TimeFormat is { } timeFormat && UserDisplayPreferences.AllowedTimeFormats.Contains(timeFormat))
        {
            settings.TimeFormat = timeFormat;
        }

        return new ClockFaceConfig
        {
            Rows =
            [
                new ClockRow { Elements = [Element("sg", 40, "dynamic"), Element("arrow", 25, "dynamic")] },
                new ClockRow { Elements = [Element("delta", 14, "dynamic", showUnits: true)] },
                new ClockRow { Elements = [Element("age", 10, color: null, opacity: 0.7)] },
            ],
            Settings = settings,
        };

        static ClockElement Element(
            string type, int size, string? color, bool? showUnits = null, double opacity = 1.0) => new()
        {
            Type = type,
            Size = size,
            ShowUnits = showUnits,
            Style = new ClockElementStyle { Color = color, Font = "system", FontWeight = "medium", Opacity = opacity },
        };
    }
}

public class ClockRow
{
    [JsonPropertyName("elements")]
    public List<ClockElement> Elements { get; set; } = [];
}

/// <summary>
/// A single element in the clock face
/// </summary>
public class ClockElement
{
    /// <summary>The hours a summary or chart element may read.</summary>
    public static readonly IReadOnlyList<int> OfferedHours = [1, 3, 6, 12, 24];

    /// <summary>The minutes ahead a forecast element may read.</summary>
    public static readonly IReadOnlyList<int> OfferedMinutesAhead = [15, 30, 45, 60];

    /// <summary>The hours a summary or chart element reads when it names none.</summary>
    public const int DefaultHours = 3;

    /// <summary>The minutes ahead a forecast element reads when it names none.</summary>
    public const int DefaultMinutesAhead = 30;

    /// <summary>
    /// A "field: message" description of an hours or minutes-ahead value the element's type reads
    /// but the editor does not offer, or null.
    /// </summary>
    public string? Validate() => Type switch
    {
        "summary" or "chart" when Hours is { } hours && !OfferedHours.Contains(hours) =>
            $"hours: must be one of {string.Join(", ", OfferedHours)}",
        "forecast" when MinutesAhead is { } minutes && !OfferedMinutesAhead.Contains(minutes) =>
            $"minutesAhead: must be one of {string.Join(", ", OfferedMinutesAhead)}",
        _ => null,
    };

    /// <summary>
    /// Element type: sg, delta, arrow, age, time, sparkline, forecast, iob, cob, basal, tracker, trackers, summary, text, chart
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Font size for text elements
    /// </summary>
    [JsonPropertyName("size")]
    public int? Size { get; set; }

    /// <summary>
    /// Whether to show units (for sg and delta elements); shown unless false
    /// </summary>
    [JsonPropertyName("showUnits")]
    public bool? ShowUnits { get; set; }

    /// <summary>
    /// Hours to display for sparkline/summary/chart (1, 3, 6, 12, 24)
    /// </summary>
    [JsonPropertyName("hours")]
    public int? Hours { get; set; }

    /// <summary>
    /// Width in pixels (for sparkline/chart)
    /// </summary>
    [JsonPropertyName("width")]
    public int? Width { get; set; }

    /// <summary>
    /// Height in pixels (for sparkline/chart)
    /// </summary>
    [JsonPropertyName("height")]
    public int? Height { get; set; }

    /// <summary>
    /// Minutes ahead for forecast (15, 30, 45, 60)
    /// </summary>
    [JsonPropertyName("minutesAhead")]
    public int? MinutesAhead { get; set; }

    /// <summary>
    /// Tracker definition ID (for single tracker element)
    /// </summary>
    [JsonPropertyName("definitionId")]
    public Guid? DefinitionId { get; set; }

    /// <summary>
    /// What to show for tracker: name, icon, remaining, urgency
    /// </summary>
    [JsonPropertyName("show")]
    public List<string>? Show { get; set; }

    /// <summary>
    /// Tracker categories to filter (for trackers element)
    /// </summary>
    [JsonPropertyName("categories")]
    public List<string>? Categories { get; set; }

    /// <summary>
    /// Visibility threshold: always, info, warn, hazard, urgent (for tracker/trackers element)
    /// </summary>
    [JsonPropertyName("visibilityThreshold")]
    public string? VisibilityThreshold { get; set; }

    /// <summary>
    /// Custom text content (for text element)
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("style")]
    public ClockElementStyle? Style { get; set; }

    [JsonPropertyName("chartConfig")]
    public ClockChartConfig? ChartConfig { get; set; }
}

public class ClockElementStyle
{
    /// <summary>
    /// Text color (hex color, "dynamic" for BG-based coloring, or "muted")
    /// </summary>
    [JsonPropertyName("color")]
    public string? Color { get; set; }

    /// <summary>
    /// Font family (system, mono, serif, sans)
    /// </summary>
    [JsonPropertyName("font")]
    public string? Font { get; set; }

    /// <summary>
    /// Font weight (normal, medium, semibold, bold)
    /// </summary>
    [JsonPropertyName("fontWeight")]
    public string? FontWeight { get; set; }

    /// <summary>
    /// Opacity (0.0-1.0, default 1.0)
    /// </summary>
    [JsonPropertyName("opacity")]
    public double? Opacity { get; set; }

    /// <summary>
    /// Additional custom CSS properties (key-value pairs)
    /// Example: { "text-shadow": "0 0 10px #000", "letter-spacing": "2px" }
    /// </summary>
    [JsonPropertyName("custom")]
    public Dictionary<string, string>? Custom { get; set; }
}

public class ClockChartConfig
{
    [JsonPropertyName("showIob")]
    public bool ShowIob { get; set; }

    [JsonPropertyName("showCob")]
    public bool ShowCob { get; set; }

    /// <summary>
    /// Show basal rate track
    /// </summary>
    [JsonPropertyName("showBasal")]
    public bool ShowBasal { get; set; }

    [JsonPropertyName("showBolus")]
    public bool ShowBolus { get; set; } = true;

    [JsonPropertyName("showCarbs")]
    public bool ShowCarbs { get; set; } = true;

    /// <summary>
    /// Show device events (sensor/site changes)
    /// </summary>
    [JsonPropertyName("showDeviceEvents")]
    public bool ShowDeviceEvents { get; set; }

    [JsonPropertyName("showAlarms")]
    public bool ShowAlarms { get; set; }

    /// <summary>
    /// Show tracker expiration markers
    /// </summary>
    [JsonPropertyName("showTrackers")]
    public bool ShowTrackers { get; set; }

    [JsonPropertyName("showPredictions")]
    public bool ShowPredictions { get; set; }

    /// <summary>
    /// Lock toggles (disable user interaction with legend)
    /// </summary>
    [JsonPropertyName("lockToggles")]
    public bool LockToggles { get; set; } = true;

    [JsonPropertyName("showLegend")]
    public bool ShowLegend { get; set; }

    /// <summary>
    /// Position chart as background (absolutely positioned behind other elements)
    /// </summary>
    [JsonPropertyName("asBackground")]
    public bool AsBackground { get; set; }
}

/// <summary>
/// Global settings for the clock face
/// </summary>
public class ClockSettings
{
    /// <summary>
    /// Use BG-colored background instead of black
    /// </summary>
    [JsonPropertyName("bgColor")]
    public bool BgColor { get; set; }

    /// <summary>
    /// Minutes after which data is considered stale
    /// </summary>
    [JsonPropertyName("staleMinutes")]
    public int StaleMinutes { get; set; } = 13;

    /// <summary>
    /// Always show time (not just when stale)
    /// </summary>
    [JsonPropertyName("alwaysShowTime")]
    public bool AlwaysShowTime { get; set; }

    /// <summary>
    /// Background image URL (optional, overrides bgColor when set)
    /// </summary>
    [JsonPropertyName("backgroundImage")]
    public string? BackgroundImage { get; set; }

    /// <summary>
    /// Background image opacity (0-100, default 100)
    /// </summary>
    [JsonPropertyName("backgroundOpacity")]
    public int BackgroundOpacity { get; set; } = 100;

    /// <summary>
    /// Enable bouncing screensaver mode on fullscreen views.
    /// </summary>
    [JsonPropertyName("screensaverMode")]
    public bool ScreensaverMode { get; set; }

    /// <summary>
    /// Glucose units the face shows: "mg/dl" or "mmol". Carried by the face rather than read from
    /// the viewer, because a clock link is opened by people who are not its owner.
    /// </summary>
    [JsonPropertyName("glucoseUnits")]
    public string GlucoseUnits { get; set; } = "mg/dl";

    /// <summary>Time format the face shows: "12" or "24". Every time element uses it.</summary>
    [JsonPropertyName("timeFormat")]
    public string TimeFormat { get; set; } = "12";

    /// <summary>
    /// Whether the face shows an alarm that is firing. Off by default: the clock link is a
    /// glucose-only capability until its owner turns this on.
    /// </summary>
    [JsonPropertyName("showAlerts")]
    public bool ShowAlerts { get; set; }

    /// <summary>
    /// Whether whoever is at the clock can record that they saw a firing alarm. The receipt names
    /// the face, not a person: anyone holding the clock link can press it. Needs
    /// <see cref="ShowAlerts"/>.
    /// </summary>
    [JsonPropertyName("allowMarkSeen")]
    public bool AllowMarkSeen { get; set; }

    /// <summary>
    /// Validates against the <see cref="UserDisplayPreferences"/> vocabulary. A face saved before
    /// these settings existed deserialises to the defaults, so only an explicit value can fail.
    /// </summary>
    public string? Validate() =>
        UserDisplayPreferences.Check("settings.glucoseUnits", GlucoseUnits ?? "null", UserDisplayPreferences.AllowedGlucoseUnits)
        ?? UserDisplayPreferences.Check("settings.timeFormat", TimeFormat ?? "null", UserDisplayPreferences.AllowedTimeFormats)
        ?? (AllowMarkSeen && !ShowAlerts ? "settings.allowMarkSeen: requires showAlerts" : null);
}

public class CreateClockFaceRequest
{
    /// <summary>
    /// User-friendly name for the clock face
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Clock face configuration. Omitted, the face starts from <see cref="ClockFaceConfig.Starter"/>
    /// with the creator's preferences.
    /// </summary>
    public ClockFaceConfig? Config { get; set; }
}

public class UpdateClockFaceRequest
{
    /// <summary>
    /// User-friendly name for the clock face (optional)
    /// </summary>
    public string? Name { get; set; }

    public ClockFaceConfig? Config { get; set; }
}

public class ClockFaceListItem
{
    public Guid Id { get; set; }

    /// <summary>
    /// User-friendly name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// DTO for public clock face retrieval (no user info)
/// </summary>
public class ClockFacePublicDto
{
    public Guid Id { get; set; }

    public ClockFaceConfig Config { get; set; } = new();
}

/// <summary>
/// Minimal glucose reading served to an anonymous public clock viewer. Carries only the
/// fields a clock face renders (value, trend, delta, time) — never raw sensor signal,
/// device, or any non-glucose data.
/// </summary>
public class ClockGlucoseDto
{
    /// <summary>Reading time as Unix milliseconds.</summary>
    public long Mills { get; set; }

    /// <summary>Glucose value in mg/dL.</summary>
    public double Mgdl { get; set; }

    /// <summary>Trend arrow direction name (e.g. "Flat", "FortyFiveUp"), or null.</summary>
    public string? Direction { get; set; }

    /// <summary>Glucose delta in mg/dL over the last reading interval, or null.</summary>
    public double? Delta { get; set; }

    /// <summary>Origin data source identifier (used to surface demo data), or null.</summary>
    public string? DataSource { get; set; }
}

/// <summary>
/// What a clock face shows beyond the latest reading, read for the elements its layout holds and
/// nothing else. A part the layout has no element for, or that its reader may not read, is null or
/// empty.
/// </summary>
/// <summary>
/// Whether the caller may add a part of a clock face, so the editor can say why before the save
/// refuses it.
/// </summary>
public class ClockPartAccessDto
{
    /// <summary>The element type.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>For a chart, the <see cref="ClockChartConfig"/> flag, as the face's JSON names it; else null.</summary>
    public string? Feature { get; set; }

    /// <summary>The scope the caller lacks to add the part, or null when the caller may add it.</summary>
    public string? MissingScope { get; set; }
}

public class ClockFaceData
{
    /// <summary>Insulin on board in units, or null when nothing stands behind a total.</summary>
    public double? Iob { get; set; }

    /// <summary>Carbs on board in grams, or null when nothing stands behind a total.</summary>
    public double? Cob { get; set; }

    /// <summary>The basal rate delivered now, or null when none is known.</summary>
    public ClockBasalDto? Basal { get; set; }

    /// <summary>One predicted value for each distinct <see cref="ClockElement.MinutesAhead"/> on the face.</summary>
    public List<ClockForecastDto> Forecasts { get; set; } = [];

    /// <summary>Time in range for each distinct summary <see cref="ClockElement.Hours"/> on the face.</summary>
    public List<ClockSummaryDto> Summaries { get; set; } = [];

    /// <summary>
    /// The running trackers the face's tracker elements can show: every one for a face with an
    /// all-trackers element, else those of the definitions its single-tracker elements name.
    /// </summary>
    public List<ClockTrackerDto> Trackers { get; set; } = [];
}

/// <summary>The basal rate delivered now.</summary>
public class ClockBasalDto
{
    /// <summary>Units per hour.</summary>
    public double Rate { get; set; }

    /// <summary>The scheduled rate at this time, when known; it differs from <see cref="Rate"/> under a temp basal.</summary>
    public double? ScheduledRate { get; set; }

    public BasalDeliveryOrigin Origin { get; set; }
}

/// <summary>A predicted glucose value some minutes from now.</summary>
public class ClockForecastDto
{
    public int MinutesAhead { get; set; }

    /// <summary>Predicted glucose in mg/dL.</summary>
    public double Mgdl { get; set; }
}

/// <summary>Time in range over the hours before now.</summary>
public class ClockSummaryDto
{
    public int Hours { get; set; }

    /// <summary>Percent of the covered time in the target range.</summary>
    public double InRangePercent { get; set; }

    /// <summary>Percent of the covered time below the target range.</summary>
    public double BelowPercent { get; set; }

    /// <summary>Percent of the covered time above the target range.</summary>
    public double AbovePercent { get; set; }

    /// <summary>Readings the percentages are measured over; zero means there is no figure to show.</summary>
    public int Readings { get; set; }
}

/// <summary>A running tracker as a clock face shows it.</summary>
public class ClockTrackerDto
{
    public Guid DefinitionId { get; set; }

    public string Name { get; set; } = string.Empty;

    public TrackerCategory Category { get; set; }

    public NotificationUrgency Urgency { get; set; }

    /// <summary>Hours until the tracker's expected end, negative once past it; null for one with no end.</summary>
    public double? HoursLeft { get; set; }
}

/// <summary>
/// An alarm that is firing, as a clock face with <see cref="ClockSettings.ShowAlerts"/> shows it:
/// what fired and since when, and nothing of who acknowledged it or how it is delivered.
/// </summary>
public class ClockAlertDto
{
    /// <summary>The excursion that is firing.</summary>
    public Guid Id { get; set; }

    /// <summary>The name of the rule that fired.</summary>
    public string RuleName { get; set; } = string.Empty;

    public AlertRuleSeverity Severity { get; set; }

    public AlertConditionType ConditionType { get; set; }

    public DateTime StartedAt { get; set; }

    /// <summary>Whether someone has acknowledged it for everyone.</summary>
    public bool Acknowledged { get; set; }

    /// <summary>Whether this clock face has recorded that it was seen.</summary>
    public bool SeenHere { get; set; }

    /// <summary>When the latest snooze on it lapses, or null when it is not snoozed.</summary>
    public DateTime? SnoozedUntil { get; set; }
}
