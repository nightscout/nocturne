using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Tools.McpServer.Services;

namespace Nocturne.Tools.McpServer.Tools;

/// <summary>
/// Legacy MCP tools for v1 entries API. Prefer the v4 tools (GlucoseTools, TreatmentTools, etc.) for new queries.
/// These are retained for backward compatibility with the original Nightscout v1 API.
/// </summary>
[McpServerToolType]
public static class EntryTools
{
    private static IApiService? _apiService;

    /// <summary>
    /// Initialize the API service (called by the DI container)
    /// </summary>
    public static void Initialize(IServiceProvider serviceProvider)
    {
        _apiService = serviceProvider.GetRequiredService<IApiService>();
    }

    private static IApiService ApiService =>
        _apiService ?? throw new InvalidOperationException("EntryTools not initialized");

    [McpServerTool]
    [Description("[Legacy v1] Get the most recent glucose reading. Prefer GetRecentGlucose (v4) for new queries.")]
    public static async Task<string> GetCurrentEntry()
    {
        try
        {
            var json = await ApiService.GetAsync("api/v1/entries/current");
            return $"Current glucose reading: {json}";
        }
        catch (Exception ex)
        {
            return $"Error retrieving current entry: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("[Legacy v1] Get recent entries. Prefer GetRecentGlucose (v4) for new queries.")]
    public static async Task<string> GetRecentEntries(
        [Description("Number of entries to retrieve (default: 24)")] int count = 24,
        [Description("Entry type filter (sgv, mbg, cal)")] string? type = null
    )
    {
        try
        {
            var endpoint = $"api/v1/entries?count={count}";
            if (!string.IsNullOrEmpty(type))
            {
                endpoint += $"&find[type]={type}";
            }

            var entries = await ApiService.GetAsync<Entry[]>(endpoint);

            if (entries == null || entries.Length == 0)
            {
                return "No entries found";
            }

            var summary = entries.Select(e => new
            {
                Id = e.Id,
                Date = e.DateString,
                Glucose = e.Mgdl,
                Direction = e.Direction,
                Type = e.Type,
                Device = e.Device,
            });

            return JsonSerializer.Serialize(
                summary,
                new JsonSerializerOptions { WriteIndented = true }
            );
        }
        catch (Exception ex)
        {
            return $"Error retrieving entries: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("[Legacy v1] Get entries by date range. Prefer GetGlucoseByDateRange (v4) for new queries.")]
    public static async Task<string> GetEntriesByDateRange(
        [Description("Start date (ISO 8601 format, e.g., 2024-01-01T00:00:00Z)")] string startDate,
        [Description("End date (ISO 8601 format, e.g., 2024-01-02T00:00:00Z)")] string endDate,
        [Description("Entry type filter (sgv, mbg, cal)")] string? type = null
    )
    {
        try
        {
            var endpoint =
                $"api/v1/entries?find[dateString][$gte]={Uri.EscapeDataString(startDate)}&find[dateString][$lte]={Uri.EscapeDataString(endDate)}";
            if (!string.IsNullOrEmpty(type))
            {
                endpoint += $"&find[type]={type}";
            }

            var entries = await ApiService.GetAsync<Entry[]>(endpoint);

            if (entries == null || entries.Length == 0)
            {
                return $"No entries found between {startDate} and {endDate}";
            }

            var summary = new
            {
                TotalEntries = entries.Length,
                DateRange = new { Start = startDate, End = endDate },
                AverageGlucose = entries.Where(e => e.Mgdl > 0).Average(e => e.Mgdl),
                MinGlucose = entries.Where(e => e.Mgdl > 0).Min(e => e.Mgdl),
                MaxGlucose = entries.Where(e => e.Mgdl > 0).Max(e => e.Mgdl),
                Entries = entries
                    .Take(10)
                    .Select(e => new
                    {
                        Date = e.DateString,
                        Glucose = e.Mgdl,
                        Direction = e.Direction,
                        Type = e.Type,
                    }),
            };

            return JsonSerializer.Serialize(
                summary,
                new JsonSerializerOptions { WriteIndented = true }
            );
        }
        catch (Exception ex)
        {
            return $"Error retrieving entries by date range: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("[Legacy v1] Get a specific entry by ID. Prefer GetGlucoseById (v4) for new queries.")]
    public static async Task<string> GetEntryById(
        [Description("The entry ID to retrieve")] string entryId
    )
    {
        try
        {
            var json = await ApiService.GetAsync($"api/v1/entries/{entryId}");
            return $"Entry details: {json}";
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404"))
        {
            return $"Entry with ID '{entryId}' not found";
        }
        catch (Exception ex)
        {
            return $"Error retrieving entry: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("[Legacy v1] Get glucose statistics. Prefer GetGlucoseStatistics in GlucoseTools (v4) for new queries.")]
    public static async Task<string> GetLegacyGlucoseStatistics(
        [Description("Number of hours to analyze (default: 24)")] int hours = 24,
        [Description("Entry type to analyze (default: sgv)")] string type = "sgv"
    )
    {
        try
        {
            var endTime = DateTime.UtcNow;
            var startTime = endTime.AddHours(-hours);

            var endpoint =
                $"api/v1/entries?find[dateString][$gte]={Uri.EscapeDataString(startTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))}&find[type]={type}";

            var entries = await ApiService.GetAsync<Entry[]>(endpoint);

            if (entries == null || entries.Length == 0)
            {
                return $"No {type} entries found in the last {hours} hours";
            }

            var glucoseValues = entries.Where(e => e.Mgdl > 0).Select(e => e.Mgdl).ToArray();

            if (glucoseValues.Length == 0)
            {
                return "No valid glucose values found";
            }

            const double targetLow = GlucoseConstants.TargetBottomMgdl;
            const double targetHigh = GlucoseConstants.TargetTopMgdl;

            var stats = new
            {
                Period = $"Last {hours} hours",
                TotalReadings = glucoseValues.Length,
                Average = Math.Round(glucoseValues.Average(), 1),
                Median = CalculateMedian(glucoseValues),
                Min = glucoseValues.Min(),
                Max = glucoseValues.Max(),
                StandardDeviation = Math.Round(CalculateStandardDeviation(glucoseValues), 1),
                TimeInRange = new
                {
                    VeryLow = glucoseValues.Count(g => g < 54),
                    Low = glucoseValues.Count(g => g >= 54 && g < targetLow),
                    InRange = glucoseValues.Count(g => g >= targetLow && g <= targetHigh),
                    High = glucoseValues.Count(g => g > targetHigh && g <= 250),
                    VeryHigh = glucoseValues.Count(g => g > 250),
                },
                TimeInRangePercentages = new
                {
                    VeryLow = Math.Round(
                        (double)glucoseValues.Count(g => g < 54) / glucoseValues.Length * 100,
                        1
                    ),
                    Low = Math.Round(
                        (double)glucoseValues.Count(g => g >= 54 && g < targetLow)
                            / glucoseValues.Length
                            * 100,
                        1
                    ),
                    InRange = Math.Round(
                        (double)glucoseValues.Count(g => g >= targetLow && g <= targetHigh)
                            / glucoseValues.Length
                            * 100,
                        1
                    ),
                    High = Math.Round(
                        (double)glucoseValues.Count(g => g > targetHigh && g <= 250)
                            / glucoseValues.Length
                            * 100,
                        1
                    ),
                    VeryHigh = Math.Round(
                        (double)glucoseValues.Count(g => g > 250) / glucoseValues.Length * 100,
                        1
                    ),
                },
            };

            return JsonSerializer.Serialize(
                stats,
                new JsonSerializerOptions { WriteIndented = true }
            );
        }
        catch (Exception ex)
        {
            return $"Error calculating glucose statistics: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("[Legacy v1] Create a new glucose entry via the v1 API.")]
    public static async Task<string> CreateEntry(
        [Description("Glucose value in mg/dL")] double glucose,
        [Description("Entry type (sgv, mbg, cal)")] string type = "sgv",
        [Description("Direction trend (Flat, SingleUp, DoubleUp, etc.)")] string? direction = null,
        [Description("Device identifier")] string? device = null,
        [Description("Date/time (ISO 8601 format, defaults to now)")] string? dateTime = null,
        [Description("Additional notes")] string? notes = null
    )
    {
        try
        {
            var entry = new Entry
            {
                Mgdl = glucose,
                Type = type,
                Direction = direction,
                Device = device,
                Notes = notes,
            };

            if (!string.IsNullOrEmpty(dateTime))
            {
                if (DateTime.TryParse(dateTime, out var parsedDate))
                {
                    entry.Date = parsedDate.ToUniversalTime();
                }
                else
                {
                    return $"Invalid date format: {dateTime}. Use ISO 8601 format (e.g., 2024-01-01T12:00:00Z)";
                }
            }
            else
            {
                entry.Date = DateTime.UtcNow;
            }

            var result = await ApiService.PostAsync("api/v1/entries", entry);
            return $"Entry created successfully: {result}";
        }
        catch (Exception ex)
        {
            return $"Error creating entry: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("[Legacy v1] Get entry count by type. Prefer v4 glucose endpoints for new queries.")]
    public static async Task<string> GetEntryCount(
        [Description("Number of hours to look back (default: 24)")] int hours = 24,
        [Description("Group by type (true/false, default: true)")] bool groupByType = true
    )
    {
        try
        {
            var endTime = DateTime.UtcNow;
            var startTime = endTime.AddHours(-hours);

            var endpoint =
                $"api/v1/entries?find[dateString][$gte]={Uri.EscapeDataString(startTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))}";

            var entries = await ApiService.GetAsync<Entry[]>(endpoint);

            if (entries == null || entries.Length == 0)
            {
                return $"No entries found in the last {hours} hours";
            }

            if (groupByType)
            {
                var grouped = entries
                    .GroupBy(e => e.Type ?? "unknown")
                    .Select(g => new { Type = g.Key, Count = g.Count() })
                    .OrderByDescending(x => x.Count);

                return JsonSerializer.Serialize(
                    new
                    {
                        Period = $"Last {hours} hours",
                        TotalEntries = entries.Length,
                        ByType = grouped,
                    },
                    new JsonSerializerOptions { WriteIndented = true }
                );
            }
            else
            {
                return $"Total entries in last {hours} hours: {entries.Length}";
            }
        }
        catch (Exception ex)
        {
            return $"Error getting entry count: {ex.Message}";
        }
    }

    private static double CalculateMedian(double[] values)
    {
        var sorted = values.OrderBy(x => x).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }

    private static double CalculateStandardDeviation(double[] values)
    {
        var mean = values.Average();
        var variance = values.Select(x => Math.Pow(x - mean, 2)).Average();
        return Math.Sqrt(variance);
    }
}
