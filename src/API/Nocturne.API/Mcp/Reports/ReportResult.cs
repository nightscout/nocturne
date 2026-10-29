using System.Text.Json.Serialization;
using Nocturne.Core.Models;

namespace Nocturne.API.Mcp.Reports;

/// <summary><c>render_report</c>'s <c>structuredContent</c>: the normalised spec and each panel's resolved data.</summary>
internal sealed record ReportResult
{
    public required ReportSpec Spec { get; init; }

    /// <summary>The tenant's glucose display unit, <c>mg/dl</c> or <c>mmol</c>.</summary>
    public required string Units { get; init; }

    public const string DefaultGlucoseColorMode = "continuous";

    /// <summary>
    /// How every glucose line in the report is coloured: <c>continuous</c> (the glucose spectrum, the
    /// dashboard's default) or <c>threshold</c> (one colour per range). One value per report, so its
    /// panels never colour glucose two ways.
    /// </summary>
    public string GlucoseColorMode { get; init; } = DefaultGlucoseColorMode;

    /// <summary>
    /// The tenant's IANA time zone, in which the summaries state local times, so the view writes
    /// times in it too; null when the tenant has none and the summaries use UTC.
    /// </summary>
    public string? TimeZone { get; init; }

    public required IReadOnlyList<ReportPanelResult> Panels { get; init; }

    /// <summary>Every panel's summary, joined: the text an assistant reads.</summary>
    public required string Summary { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<ReportPanelStatus>))]
internal enum ReportPanelStatus
{
    /// <summary>Read and resolved.</summary>
    [JsonStringEnumMemberName("ok")]
    Ok,

    /// <summary>Not read: the token lacks the dataset's scope.</summary>
    [JsonStringEnumMemberName("not_permitted")]
    NotPermitted,

    /// <summary>Not finished within the report's time limit.</summary>
    [JsonStringEnumMemberName("timed_out")]
    TimedOut,
}

internal sealed record ReportPanelResult
{
    public required string Id { get; init; }

    public string? Title { get; init; }

    public required string Dataset { get; init; }

    public required ReportPrimitive Primitive { get; init; }

    public required ReportBindings Bindings { get; init; }

    public required ReportPanelStatus Status { get; init; }

    /// <summary>The resolved range, Unix milliseconds.</summary>
    public required ReportWindow Window { get; init; }

    /// <summary>The catalogue entries of the fields the bindings draw and the rows carry.</summary>
    public IReadOnlyList<ReportField> Fields { get; init; } = [];

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; } = [];

    public ReportThresholds? Thresholds { get; init; }

    /// <summary>The dashboard chart payload, for <see cref="ReportPrimitive.GlucoseChart"/> only.</summary>
    public DashboardChartData? Chart { get; init; }

    public IReadOnlyList<string> OmittedScopes { get; init; } = [];

    public required string Summary { get; init; }

    /// <summary>The range was cut to the last 24 hours, all a history-clamped caller reads.</summary>
    public bool Limited { get; init; }

    /// <summary>Fields left out because the token lacks their scope.</summary>
    public IReadOnlyList<ReportOmittedField> Omitted { get; init; } = [];

    public ReportResolution? Resolution { get; init; }

    /// <summary>The web report that shows this panel's data, with its range and parameters; null where there is none.</summary>
    public string? Link { get; init; }
}

internal sealed record ReportWindow(long From, long To);
