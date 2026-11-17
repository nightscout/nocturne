using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Nocturne.Tools.McpServer.Tools;

/// <summary>
/// MCP tools for querying v4 device status (APS, pump, uploader snapshots).
/// </summary>
[McpServerToolType]
public static class DeviceStatusTools
{
    [McpServerTool]
    [Description("Get recent APS (automated insulin delivery / closed loop) snapshots. Shows loop decisions, IOB, COB, predicted glucose.")]
    public static async Task<string> GetApsSnapshots(
        TimeProvider timeProvider,
        [Description("Number of hours to look back (default: 6)")] int hours = 6,
        [Description("Maximum number of snapshots (default: 20)")] int limit = 20
    )
    {
        try
        {
            var from = ToolBase.ToIso(timeProvider.GetUtcNow().UtcDateTime.AddHours(-hours));
            var endpoint = ToolBase.BuildQuery("api/v4/device-status/aps", new()
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
            return $"Error retrieving APS snapshots: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Get recent pump status snapshots (reservoir level, battery, status).")]
    public static async Task<string> GetPumpSnapshots(
        TimeProvider timeProvider,
        [Description("Number of hours to look back (default: 6)")] int hours = 6,
        [Description("Maximum number of snapshots (default: 20)")] int limit = 20
    )
    {
        try
        {
            var from = ToolBase.ToIso(timeProvider.GetUtcNow().UtcDateTime.AddHours(-hours));
            var endpoint = ToolBase.BuildQuery("api/v4/device-status/pump", new()
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
            return $"Error retrieving pump snapshots: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Get recent uploader status snapshots (phone/uploader device battery, connectivity).")]
    public static async Task<string> GetUploaderSnapshots(
        TimeProvider timeProvider,
        [Description("Number of hours to look back (default: 6)")] int hours = 6,
        [Description("Maximum number of snapshots (default: 20)")] int limit = 20
    )
    {
        try
        {
            var from = ToolBase.ToIso(timeProvider.GetUtcNow().UtcDateTime.AddHours(-hours));
            var endpoint = ToolBase.BuildQuery("api/v4/device-status/uploader", new()
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
            return $"Error retrieving uploader snapshots: {ex.Message}";
        }
    }
}
