using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Nocturne.Tools.McpServer.Tools;

/// <summary>
/// MCP tools for querying v4 observations (notes, BG checks, device events).
/// </summary>
[McpServerToolType]
public static class ObservationTools
{
    [McpServerTool]
    [Description("Get recent timestamped notes/annotations.")]
    public static async Task<string> GetRecentNotes(
        TimeProvider timeProvider,
        [Description("Number of hours to look back (default: 24)")] int hours = 24,
        [Description("Maximum number of notes (default: 50)")] int limit = 50
    )
    {
        try
        {
            var from = ToolBase.ToIso(timeProvider.GetUtcNow().UtcDateTime.AddHours(-hours));
            var endpoint = ToolBase.BuildQuery("api/v4/observations/notes", new()
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
            return $"Error retrieving notes: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Get recent blood glucose (fingerstick) check observations.")]
    public static async Task<string> GetBgChecks(
        TimeProvider timeProvider,
        [Description("Number of hours to look back (default: 24)")] int hours = 24,
        [Description("Maximum number of records (default: 50)")] int limit = 50
    )
    {
        try
        {
            var from = ToolBase.ToIso(timeProvider.GetUtcNow().UtcDateTime.AddHours(-hours));
            var endpoint = ToolBase.BuildQuery("api/v4/observations/bg-checks", new()
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
            return $"Error retrieving BG checks: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Get recent device events (sensor changes, pump events, etc.).")]
    public static async Task<string> GetDeviceEvents(
        TimeProvider timeProvider,
        [Description("Number of hours to look back (default: 24)")] int hours = 24,
        [Description("Maximum number of records (default: 50)")] int limit = 50
    )
    {
        try
        {
            var from = ToolBase.ToIso(timeProvider.GetUtcNow().UtcDateTime.AddHours(-hours));
            var endpoint = ToolBase.BuildQuery("api/v4/observations/device-events", new()
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
            return $"Error retrieving device events: {ex.Message}";
        }
    }
}
