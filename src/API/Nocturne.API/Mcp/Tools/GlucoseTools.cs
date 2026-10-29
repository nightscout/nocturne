using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nocturne.API.Mcp.Reports;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Mcp.Tools;

[McpServerToolType]
internal sealed class GlucoseTools
{
    internal const int MaxHours = 24;

    [McpServerTool(Name = "show_glucose", Title = "Show glucose", ReadOnly = true, OpenWorld = false)]
    [McpAppUi(ResourceUri = ReportResources.ReportUri)]
    [Description(
        "Shows the glucose chart for the last few hours, with insulin, carbs, basal, pump modes, "
        + "profiles, heart rate, steps and sleep where the token's scopes allow. Returns a text "
        + "summary: range, min, max, mean, time in range, below and above range, low and high episodes, "
        + "and the number of boluses and carb entries. For other ranges or more panels, use render_report.")]
    [McpRequireScope(Scope.GlucoseRead)]
    public static async Task<CallToolResult> ShowGlucose(
        ReportRenderer renderer,
        [Description("How many hours back from now to show, 1 to 24.")] int hours = 6,
        CancellationToken cancellationToken = default)
    {
        if (hours is < 1 or > MaxHours)
            throw new McpInvalidArgumentsException($"hours must be between 1 and {MaxHours}.");

        return (await renderer.RenderAsync(Preset(hours), cancellationToken)).Result;
    }

    /// <summary>The spec <c>show_glucose</c> draws: one dashboard chart of the last <paramref name="hours"/> hours.</summary>
    internal static ReportSpec Preset(int hours) => new()
    {
        Version = ReportSpec.CurrentVersion,
        Title = null,
        Range = new ReportRange { Hours = hours },
        Panels =
        [
            new ReportPanelSpec
            {
                Id = "glucose",
                Dataset = new GlucoseSeriesParameters(),
                Primitive = ReportPrimitive.GlucoseChart,
                Bindings = new ReportBindings(),
                Range = new ReportRange { Hours = hours },
            },
        ],
    };

    /// <summary>
    /// Every dataset the redacted payload carries, with its record count. Redaction has already
    /// emptied what the token's scopes do not cover, so an empty dataset is left out.
    /// </summary>
    internal static void RecordReads(DashboardChartData data, McpToolReadLog readLog)
    {
        void Read(string dataset, int records)
        {
            if (records > 0)
                readLog.Record(dataset, records);
        }

        Read("glucose", data.GlucoseData.Count + data.BgCheckMarkers.Count);
        Read("treatments",
            data.BolusMarkers.Count + data.CarbMarkers.Count + data.BasalInjectionMarkers.Count
            + data.BasalSeries.Count + data.IobSeries.Count + data.CobSeries.Count
            + data.TempBasalSpans.Count + data.BasalDeliverySpans.Count + data.OverrideSpans.Count
            + data.ActivitySpans.Count(s => s.Kind != ChartSpanKind.Sleep));
        Read("device_events", data.DeviceEventMarkers.Count + data.SystemEventMarkers.Count);
        Read("pump_modes", data.PumpModeSpans.Count);
        Read("profiles", data.ProfileSpans.Count);
        Read("heart_rate", data.HeartRateSeries.Count);
        Read("steps", data.StepSeries.Count);
        Read("sleep", data.ActivitySpans.Count(s => s.Kind == ChartSpanKind.Sleep));
        Read("trackers", data.TrackerMarkers.Count);
    }
}
