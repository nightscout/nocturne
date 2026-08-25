using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Nocturne.Core.Constants;

namespace Nocturne.Tools.McpServer.Tools;

/// <summary>
/// MCP tools for querying v4 glucose data (sensor, meter, calibrations).
/// </summary>
[McpServerToolType]
public static class GlucoseTools
{
    [McpServerTool]
    [Description("Get recent sensor glucose (CGM) readings. Returns timestamped glucose values with trend direction.")]
    public static async Task<string> GetRecentGlucose(
        [Description("Number of hours to look back (default: 6)")] int hours = 6,
        [Description("Maximum number of readings to return (default: 50, max: 500)")] int limit = 50,
        [Description("Sort order: timestamp_desc (newest first) or timestamp_asc (oldest first)")] string sort = "timestamp_desc"
    )
    {
        try
        {
            var from = ToolBase.ToIso(DateTime.UtcNow.AddHours(-hours));
            var endpoint = ToolBase.BuildQuery("api/v4/glucose/sensor", new()
            {
                ["from"] = from,
                ["limit"] = Math.Min(limit, 500).ToString(),
                ["sort"] = sort,
            });

            var json = await ToolBase.Api.GetAsync(endpoint);
            return json;
        }
        catch (Exception ex)
        {
            return $"Error retrieving sensor glucose: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Get sensor glucose readings within a specific date range.")]
    public static async Task<string> GetGlucoseByDateRange(
        [Description("Start date/time (ISO 8601 format, e.g. 2024-01-01T00:00:00Z)")] string from,
        [Description("End date/time (ISO 8601 format, e.g. 2024-01-02T00:00:00Z)")] string to,
        [Description("Maximum number of readings (default: 100)")] int limit = 100,
        [Description("Sort order: timestamp_desc or timestamp_asc")] string sort = "timestamp_desc"
    )
    {
        try
        {
            var endpoint = ToolBase.BuildQuery("api/v4/glucose/sensor", new()
            {
                ["from"] = from,
                ["to"] = to,
                ["limit"] = limit.ToString(),
                ["sort"] = sort,
            });

            var json = await ToolBase.Api.GetAsync(endpoint);
            return json;
        }
        catch (Exception ex)
        {
            return $"Error retrieving glucose by date range: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Calculate glucose statistics (average, median, min, max, standard deviation, time-in-range) from recent sensor readings.")]
    public static async Task<string> GetGlucoseStatistics(
        [Description("Number of hours to analyze (default: 24)")] int hours = 24
    )
    {
        try
        {
            var from = ToolBase.ToIso(DateTime.UtcNow.AddHours(-hours));
            var endpoint = ToolBase.BuildQuery("api/v4/glucose/sensor", new()
            {
                ["from"] = from,
                ["limit"] = "500",
                ["sort"] = "timestamp_desc",
            });

            var json = await ToolBase.Api.GetAsync(endpoint);
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var dataElement))
                return "No glucose data returned";

            var values = new List<double>();
            foreach (var item in dataElement.EnumerateArray())
            {
                if (item.TryGetProperty("mgdl", out var mgdl) && mgdl.TryGetDouble(out var v) && v > 0)
                    values.Add(v);
                else if (item.TryGetProperty("value", out var val) && val.TryGetDouble(out var v2) && v2 > 0)
                    values.Add(v2);
            }

            if (values.Count == 0)
                return $"No valid glucose values found in the last {hours} hours";

            var sorted = values.OrderBy(x => x).ToArray();
            var mean = sorted.Average();
            var mid = sorted.Length / 2;
            var median = sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
            var variance = sorted.Select(x => Math.Pow(x - mean, 2)).Average();
            var stdDev = Math.Sqrt(variance);

            const double targetLow = GlucoseConstants.TargetBottomMgdl;
            const double targetHigh = GlucoseConstants.TargetTopMgdl;

            var stats = new
            {
                Period = $"Last {hours} hours",
                TotalReadings = values.Count,
                Average = Math.Round(mean, 1),
                Median = Math.Round(median, 1),
                Min = sorted[0],
                Max = sorted[^1],
                StandardDeviation = Math.Round(stdDev, 1),
                CoefficientOfVariation = Math.Round(stdDev / mean * 100, 1),
                TimeInRange = new
                {
                    VeryLow = sorted.Count(g => g < 54),
                    Low = sorted.Count(g => g >= 54 && g < targetLow),
                    InRange = sorted.Count(g => g >= targetLow && g <= targetHigh),
                    High = sorted.Count(g => g > targetHigh && g <= 250),
                    VeryHigh = sorted.Count(g => g > 250),
                },
                TimeInRangePercent = new
                {
                    VeryLow = Math.Round((double)sorted.Count(g => g < 54) / values.Count * 100, 1),
                    Low = Math.Round((double)sorted.Count(g => g >= 54 && g < targetLow) / values.Count * 100, 1),
                    InRange = Math.Round((double)sorted.Count(g => g >= targetLow && g <= targetHigh) / values.Count * 100, 1),
                    High = Math.Round((double)sorted.Count(g => g > targetHigh && g <= 250) / values.Count * 100, 1),
                    VeryHigh = Math.Round((double)sorted.Count(g => g > 250) / values.Count * 100, 1),
                },
            };

            return ToolBase.Serialize(stats);
        }
        catch (Exception ex)
        {
            return $"Error calculating glucose statistics: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Get recent meter (fingerstick) glucose readings.")]
    public static async Task<string> GetMeterReadings(
        [Description("Number of hours to look back (default: 24)")] int hours = 24,
        [Description("Maximum number of readings (default: 50)")] int limit = 50
    )
    {
        try
        {
            var from = ToolBase.ToIso(DateTime.UtcNow.AddHours(-hours));
            var endpoint = ToolBase.BuildQuery("api/v4/glucose/meter", new()
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
            return $"Error retrieving meter readings: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Get a specific glucose reading by its ID.")]
    public static async Task<string> GetGlucoseById(
        [Description("The glucose reading ID (GUID)")] string id,
        [Description("Type: sensor, meter, or calibrations")] string type = "sensor"
    )
    {
        try
        {
            var json = await ToolBase.Api.GetAsync($"api/v4/glucose/{type}/{id}");
            return json;
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404"))
        {
            return $"Glucose reading '{id}' not found";
        }
        catch (Exception ex)
        {
            return $"Error retrieving glucose reading: {ex.Message}";
        }
    }
}
