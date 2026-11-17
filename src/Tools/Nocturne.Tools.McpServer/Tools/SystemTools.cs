using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Nocturne.Tools.McpServer.Tools;

/// <summary>
/// MCP tools for system status and events.
/// </summary>
[McpServerToolType]
public static class SystemTools
{
    [McpServerTool]
    [Description("Get the current Nocturne system status (server info, uptime, connected services).")]
    public static async Task<string> GetSystemStatus()
    {
        try
        {
            var json = await ToolBase.Api.GetAsync("api/v4/status");
            return json;
        }
        catch (Exception ex)
        {
            return $"Error retrieving system status: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Check if the Nocturne API is healthy and responding.")]
    public static async Task<string> HealthCheck()
    {
        try
        {
            var json = await ToolBase.Api.GetAsync("api/v4/status/health");
            return json;
        }
        catch (Exception ex)
        {
            return $"API health check failed: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Get recent system events (alarms, warnings, informational events).")]
    public static async Task<string> GetSystemEvents(
        TimeProvider timeProvider,
        [Description("Number of hours to look back (default: 24)")] int hours = 24,
        [Description("Maximum number of events (default: 50)")] int limit = 50
    )
    {
        try
        {
            var from = ToolBase.ToIso(timeProvider.GetUtcNow().UtcDateTime.AddHours(-hours));
            var endpoint = ToolBase.BuildQuery("api/v4/system-events", new()
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
            return $"Error retrieving system events: {ex.Message}";
        }
    }
}
