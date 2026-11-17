using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Nocturne.Tools.McpServer.Tools;

/// <summary>
/// MCP tools for querying v4 state spans (time-ranged system states like pump modes, activities, connectivity).
/// </summary>
[McpServerToolType]
public static class StateSpanTools
{
    [McpServerTool]
    [Description("Get state spans (time-ranged records). Categories include: PumpMode, PumpConnectivity, Override, Profile, Sleep, Exercise, Illness, Travel.")]
    public static async Task<string> GetStateSpans(
        TimeProvider timeProvider,
        [Description("Category filter (e.g. PumpMode, Sleep, Exercise, Override). Leave empty for all.")] string? category = null,
        [Description("Number of hours to look back (default: 24)")] int hours = 24,
        [Description("Maximum number of spans (default: 100)")] int count = 100
    )
    {
        try
        {
            var from = ToolBase.ToIso(timeProvider.GetUtcNow().UtcDateTime.AddHours(-hours));
            var endpoint = ToolBase.BuildQuery("api/v4/state-spans", new()
            {
                ["category"] = category,
                ["from"] = from,
                ["count"] = count.ToString(),
            });

            var json = await ToolBase.Api.GetAsync(endpoint);
            return json;
        }
        catch (Exception ex)
        {
            return $"Error retrieving state spans: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Get pump mode history (Auto, Manual, etc.).")]
    public static async Task<string> GetPumpModes(
        TimeProvider timeProvider,
        [Description("Number of hours to look back (default: 24)")] int hours = 24
    )
    {
        try
        {
            var from = ToolBase.ToIso(timeProvider.GetUtcNow().UtcDateTime.AddHours(-hours));
            var endpoint = ToolBase.BuildQuery("api/v4/state-spans/pump-modes", new()
            {
                ["from"] = from,
            });

            var json = await ToolBase.Api.GetAsync(endpoint);
            return json;
        }
        catch (Exception ex)
        {
            return $"Error retrieving pump modes: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Get all activity state spans (sleep, exercise, illness, travel periods).")]
    public static async Task<string> GetActivities(
        TimeProvider timeProvider,
        [Description("Number of hours to look back (default: 24)")] int hours = 24
    )
    {
        try
        {
            var from = ToolBase.ToIso(timeProvider.GetUtcNow().UtcDateTime.AddHours(-hours));
            var endpoint = ToolBase.BuildQuery("api/v4/state-spans/activities", new()
            {
                ["from"] = from,
            });

            var json = await ToolBase.Api.GetAsync(endpoint);
            return json;
        }
        catch (Exception ex)
        {
            return $"Error retrieving activities: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Get connectivity state spans (sensor/pump connection status).")]
    public static async Task<string> GetConnectivity(
        TimeProvider timeProvider,
        [Description("Number of hours to look back (default: 24)")] int hours = 24
    )
    {
        try
        {
            var from = ToolBase.ToIso(timeProvider.GetUtcNow().UtcDateTime.AddHours(-hours));
            var endpoint = ToolBase.BuildQuery("api/v4/state-spans/connectivity", new()
            {
                ["from"] = from,
            });

            var json = await ToolBase.Api.GetAsync(endpoint);
            return json;
        }
        catch (Exception ex)
        {
            return $"Error retrieving connectivity spans: {ex.Message}";
        }
    }
}
