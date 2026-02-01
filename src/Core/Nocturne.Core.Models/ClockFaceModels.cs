using System.Text.Json.Serialization;

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

    /// <summary>
    /// When this clock face was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When this clock face was last updated
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Configuration for a clock face layout
/// </summary>
public class ClockFaceConfig
{
    /// <summary>
    /// Rows of elements (implicit row breaks based on element grouping)
    /// </summary>
    [JsonPropertyName("rows")]
    public List<ClockRow> Rows { get; set; } = [];

    /// <summary>
    /// Global settings for the clock face
    /// </summary>
    [JsonPropertyName("settings")]
    public ClockSettings Settings { get; set; } = new();
}

/// <summary>
/// A row of elements in the clock face
/// </summary>
public class ClockRow
{
    /// <summary>
    /// Elements in this row
    /// </summary>
    [JsonPropertyName("elements")]
    public List<ClockElement> Elements { get; set; } = [];
}

/// <summary>
/// A single element in the clock face
/// </summary>
public class ClockElement
{
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
    /// Whether to show units (for delta element)
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
    /// Time format for time element (12h or 24h)
    /// </summary>
    [JsonPropertyName("format")]
    public string? Format { get; set; }

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

    /// <summary>
    /// Styling configuration for the element
    /// </summary>
    [JsonPropertyName("style")]
    public ClockElementStyle? Style { get; set; }

    /// <summary>
    /// Chart configuration (for chart element)
    /// </summary>
    [JsonPropertyName("chartConfig")]
    public ClockChartConfig? ChartConfig { get; set; }
}

/// <summary>
/// Styling configuration for clock elements
/// </summary>
public class ClockElementStyle
{
    /// <summary>
    /// Text color (hex color, or "dynamic" for BG-based coloring)
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

/// <summary>
/// Configuration for an embedded chart element
/// </summary>
public class ClockChartConfig
{
    /// <summary>
    /// Show IOB track
    /// </summary>
    [JsonPropertyName("showIob")]
    public bool ShowIob { get; set; }

    /// <summary>
    /// Show COB track
    /// </summary>
    [JsonPropertyName("showCob")]
    public bool ShowCob { get; set; }

    /// <summary>
    /// Show basal rate track
    /// </summary>
    [JsonPropertyName("showBasal")]
    public bool ShowBasal { get; set; }

    /// <summary>
    /// Show bolus markers
    /// </summary>
    [JsonPropertyName("showBolus")]
    public bool ShowBolus { get; set; } = true;

    /// <summary>
    /// Show carb markers
    /// </summary>
    [JsonPropertyName("showCarbs")]
    public bool ShowCarbs { get; set; } = true;

    /// <summary>
    /// Show device events (sensor/site changes)
    /// </summary>
    [JsonPropertyName("showDeviceEvents")]
    public bool ShowDeviceEvents { get; set; }

    /// <summary>
    /// Show alarm markers
    /// </summary>
    [JsonPropertyName("showAlarms")]
    public bool ShowAlarms { get; set; }

    /// <summary>
    /// Show tracker expiration markers
    /// </summary>
    [JsonPropertyName("showTrackers")]
    public bool ShowTrackers { get; set; }

    /// <summary>
    /// Show prediction lines
    /// </summary>
    [JsonPropertyName("showPredictions")]
    public bool ShowPredictions { get; set; }

    /// <summary>
    /// Lock toggles (disable user interaction with legend)
    /// </summary>
    [JsonPropertyName("lockToggles")]
    public bool LockToggles { get; set; } = true;

    /// <summary>
    /// Show legend controls
    /// </summary>
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
}

/// <summary>
/// DTO for creating a new clock face
/// </summary>
public class CreateClockFaceRequest
{
    /// <summary>
    /// User-friendly name for the clock face
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Clock face configuration
    /// </summary>
    public ClockFaceConfig Config { get; set; } = new();
}

/// <summary>
/// DTO for updating a clock face
/// </summary>
public class UpdateClockFaceRequest
{
    /// <summary>
    /// User-friendly name for the clock face (optional)
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Clock face configuration (optional)
    /// </summary>
    public ClockFaceConfig? Config { get; set; }
}

/// <summary>
/// DTO for clock face list item
/// </summary>
public class ClockFaceListItem
{
    /// <summary>
    /// Clock face ID
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// User-friendly name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// When created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When last updated
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// DTO for public clock face retrieval (no user info)
/// </summary>
public class ClockFacePublicDto
{
    /// <summary>
    /// Clock face ID
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Clock face configuration
    /// </summary>
    public ClockFaceConfig Config { get; set; } = new();
}
