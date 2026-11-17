using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Nocturne.Tools.McpServer.Tools;

/// <summary>
/// MCP tools for querying v4 nutrition data (carb intakes, meals, foods).
/// </summary>
[McpServerToolType]
public static class NutritionTools
{
    [McpServerTool]
    [Description("Get recent carbohydrate intake records.")]
    public static async Task<string> GetRecentCarbs(
        TimeProvider timeProvider,
        [Description("Number of hours to look back (default: 24)")] int hours = 24,
        [Description("Maximum number of records (default: 50)")] int limit = 50
    )
    {
        try
        {
            var from = ToolBase.ToIso(timeProvider.GetUtcNow().UtcDateTime.AddHours(-hours));
            var endpoint = ToolBase.BuildQuery("api/v4/nutrition/carbs", new()
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
            return $"Error retrieving carb intakes: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Get recent meals with food attribution status (which foods were logged with each meal).")]
    public static async Task<string> GetRecentMeals(
        TimeProvider timeProvider,
        [Description("Number of hours to look back (default: 24)")] int hours = 24
    )
    {
        try
        {
            var from = ToolBase.ToIso(timeProvider.GetUtcNow().UtcDateTime.AddHours(-hours));
            var endpoint = ToolBase.BuildQuery("api/v4/nutrition/meals", new()
            {
                ["from"] = from,
            });

            var json = await ToolBase.Api.GetAsync(endpoint);
            return json;
        }
        catch (Exception ex)
        {
            return $"Error retrieving meals: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Get the user's favorite foods.")]
    public static async Task<string> GetFavoriteFoods()
    {
        try
        {
            var json = await ToolBase.Api.GetAsync("api/v4/foods/favorites");
            return json;
        }
        catch (Exception ex)
        {
            return $"Error retrieving favorite foods: {ex.Message}";
        }
    }
}
