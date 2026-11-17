using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Nocturne.Tools.McpServer.Tools;

/// <summary>
/// MCP tools for querying v4 insulin data (boluses, bolus calculations).
/// </summary>
[McpServerToolType]
public static class InsulinTools
{
    [McpServerTool]
    [Description("Get recent insulin bolus records (delivered bolus doses).")]
    public static async Task<string> GetRecentBoluses(
        TimeProvider timeProvider,
        [Description("Number of hours to look back (default: 24)")] int hours = 24,
        [Description("Maximum number of records (default: 50)")] int limit = 50
    )
    {
        try
        {
            var from = ToolBase.ToIso(timeProvider.GetUtcNow().UtcDateTime.AddHours(-hours));
            var endpoint = ToolBase.BuildQuery("api/v4/insulin/boluses", new()
            {
                ["from"] = from,
                ["limit"] = limit.ToString(),
                ["sort"] = "timestamp_desc",
            });

            var json = await ToolBase.Api.GetAsync(endpoint);
            return json;
        }
        catch (Exception ex)
        {
            return $"Error retrieving boluses: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Get recent bolus calculator/wizard records (the calculations used to determine bolus doses).")]
    public static async Task<string> GetBolusCalculations(
        TimeProvider timeProvider,
        [Description("Number of hours to look back (default: 24)")] int hours = 24,
        [Description("Maximum number of records (default: 50)")] int limit = 50
    )
    {
        try
        {
            var from = ToolBase.ToIso(timeProvider.GetUtcNow().UtcDateTime.AddHours(-hours));
            var endpoint = ToolBase.BuildQuery("api/v4/insulin/calculations", new()
            {
                ["from"] = from,
                ["limit"] = limit.ToString(),
                ["sort"] = "timestamp_desc",
            });

            var json = await ToolBase.Api.GetAsync(endpoint);
            return json;
        }
        catch (Exception ex)
        {
            return $"Error retrieving bolus calculations: {ex.Message}";
        }
    }
}
