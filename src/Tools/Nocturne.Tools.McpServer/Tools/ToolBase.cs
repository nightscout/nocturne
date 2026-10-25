using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Tools.McpServer.Services;

namespace Nocturne.Tools.McpServer.Tools;

/// <summary>
/// Shared initialization and utilities for MCP tool classes.
/// Each tool class should call <see cref="Initialize"/> once at startup.
/// </summary>
public static class ToolBase
{
    private static IApiService? _apiService;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static void Initialize(IServiceProvider serviceProvider)
    {
        _apiService = serviceProvider.GetRequiredService<IApiService>();
    }

    internal static IApiService Api =>
        _apiService ?? throw new InvalidOperationException("ToolBase not initialized. Call ToolBase.Initialize first.");

    internal static string ToIso(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    internal static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>
    /// The time in range of <paramref name="readings"/> as the API computes it, over the minutes
    /// they cover and at the consensus bounds the reports use.
    /// </summary>
    internal static async Task<TimeInRangeSummary> TimeInRangeAsync(
        IApiService api,
        IEnumerable<SensorGlucose> readings)
    {
        var json = await api.PostAsync("api/v4/statistics/time-in-range", new { entries = readings });
        var metrics = JsonSerializer.Deserialize<TimeInRangeMetrics>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("The time-in-range response was empty.");
        return new TimeInRangeSummary(
            new
            {
                VeryLow = Math.Round(metrics.Durations.VeryLow, 1),
                Low = Math.Round(metrics.Durations.Low, 1),
                InRange = Math.Round(metrics.Durations.Target, 1),
                High = Math.Round(metrics.Durations.High, 1),
                VeryHigh = Math.Round(metrics.Durations.VeryHigh, 1),
            },
            new
            {
                VeryLow = Math.Round(metrics.Percentages.VeryLow, 1),
                Low = Math.Round(metrics.Percentages.Low, 1),
                InRange = Math.Round(metrics.Percentages.Target, 1),
                High = Math.Round(metrics.Percentages.High, 1),
                VeryHigh = Math.Round(metrics.Percentages.VeryHigh, 1),
            });
    }

    internal readonly record struct TimeInRangeSummary(object Minutes, object Percent);

    internal static string BuildQuery(string basePath, Dictionary<string, string?> parameters)
    {
        var pairs = parameters
            .Where(p => p.Value != null)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}");

        var query = string.Join("&", pairs);
        return string.IsNullOrEmpty(query) ? basePath : $"{basePath}?{query}";
    }
}
