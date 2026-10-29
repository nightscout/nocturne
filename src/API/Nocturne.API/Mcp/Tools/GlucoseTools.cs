using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

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
        + "summary: range, min, max, mean, time in range, readings below and above range, and the "
        + "number of boluses and carb entries.")]
    [McpRequireScope(Scope.GlucoseRead)]
    public static async Task<CallToolResult> ShowGlucose(
        IHttpContextAccessor httpContextAccessor,
        IChartDataService chartData,
        ITenantAppearanceReader appearance,
        ITenantAccessor tenant,
        IOptions<MvcJsonOptions> jsonOptions,
        McpToolReadLog readLog,
        TimeProvider time,
        [Description("How many hours back from now to show, 1 to 24.")] int hours = 6,
        CancellationToken cancellationToken = default)
    {
        if (hours is < 1 or > MaxHours)
            throw new McpException($"hours must be between 1 and {MaxHours}.");

        var http = httpContextAccessor.HttpContext!;
        var to = time.GetUtcNow();
        var from = to.AddHours(-hours);

        var data = await chartData.GetDashboardChartDataAsync(
            from.ToUnixTimeMilliseconds(),
            to.ToUnixTimeMilliseconds(),
            intervalMinutes: 5,
            includeHealthSeries: true,
            cancellationToken);
        data = ChartDataReadScopeGuard.Redact(data, http.GetGrantedScopes());
        if (!TrackerReadAccess.Allows(http))
            data.TrackerMarkers = [];

        readLog.Record("glucose", data.GlucoseData.Count);
        if (http.HasScope(Scope.TreatmentsRead))
            readLog.Record("treatments", data.BolusMarkers.Count + data.CarbMarkers.Count);

        var useMmol = await GlucoseUnits.UsesMmolAsync(appearance, tenant, cancellationToken);
        var omitted = GlucoseSummary.OmittedTracks(http.HasScope);

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = GlucoseSummary.Build(data, useMmol, from, to, omitted) },
            ],
            StructuredContent = JsonSerializer.SerializeToElement(
                new
                {
                    kind = "glucose",
                    units = useMmol ? "mmol" : "mg/dl",
                    window = new { from = from.ToUnixTimeMilliseconds(), to = to.ToUnixTimeMilliseconds() },
                    omittedTracks = omitted,
                    data,
                },
                jsonOptions.Value.JsonSerializerOptions),
        };
    }
}
