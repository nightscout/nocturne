using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Nocturne.Tools.McpServer.Tools;

/// <summary>
/// MCP tools for the consolidated dashboard chart data endpoint.
/// </summary>
[McpServerToolType]
public static class ChartDataTools
{
    [McpServerTool]
    [Description("Get consolidated dashboard data including glucose readings, IOB/COB series, basal delivery, treatment markers, state spans, system events, and tracker markers. This is the most comprehensive single-call data source.")]
    public static async Task<string> GetDashboardData(
        TimeProvider timeProvider,
        [Description("Number of hours to look back (default: 6, max: 72)")] int hours = 6,
        [Description("Data interval in minutes (default: 5, range: 1-60)")] int intervalMinutes = 5
    )
    {
        try
        {
            var now = timeProvider.GetUtcNow();
            var endTime = now.ToUnixTimeMilliseconds();
            var startTime = now.AddHours(-Math.Min(hours, 72)).ToUnixTimeMilliseconds();

            var endpoint = ToolBase.BuildQuery("api/v4/chart-data/dashboard", new()
            {
                ["startTime"] = startTime.ToString(),
                ["endTime"] = endTime.ToString(),
                ["intervalMinutes"] = Math.Clamp(intervalMinutes, 1, 60).ToString(),
            });

            var json = await ToolBase.Api.GetAsync(endpoint);
            return json;
        }
        catch (Exception ex)
        {
            return $"Error retrieving dashboard data: {ex.Message}";
        }
    }
}
