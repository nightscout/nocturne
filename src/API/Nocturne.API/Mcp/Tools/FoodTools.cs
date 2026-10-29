using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Nocturne.API.Mcp.Tools;

/// <summary><c>find_foods</c>: the food catalog and the caller's favorites, for a meal cohort's food filter.</summary>
[McpServerToolType]
internal sealed class FoodTools
{
    internal const int MaxResults = 25;

    /// <summary>How far back a food's uses are counted.</summary>
    internal static readonly TimeSpan UsageWindow = TimeSpan.FromDays(90);

    [McpServerTool(Name = "find_foods", Title = "Find foods", ReadOnly = true, OpenWorld = false, Idempotent = true)]
    [Description(
        "Searches the food catalog and the user's favorite foods by name, category or subcategory, and returns each "
        + "food's id, name and category, whether it is a favorite, and how many carb entries of the last 90 days carry a "
        + "food record of it. Use the ids or the category in an event_response meal cohort's 'foods' or 'foodCategory'. "
        + "Meals logged as carbs only carry no food record and cannot match a food filter. At most 25 foods, most used first.")]
    [McpRequireScope(Scope.FoodRead)]
    public static async Task<CallToolResult> FindFoods(
        IFoodRepository foods,
        IUserFoodFavoriteService favorites,
        ITreatmentFoodRepository treatmentFoods,
        IHttpContextAccessor httpContextAccessor,
        McpToolReadLog readLog,
        TimeProvider time,
        IOptions<MvcJsonOptions> jsonOptions,
        [Description("Text to look for in a food's name, category or subcategory. Empty for the most used foods.")] string? query = null,
        CancellationToken cancellationToken = default)
    {
        var text = query?.Trim() ?? "";
        if (text.Length > 100)
            throw new McpInvalidArgumentsException("query is at most 100 characters.");

        var found = (await foods.SearchFoodAsync(text, 200, 0, cancellationToken)).ToList();
        var subject = httpContextAccessor.HttpContext?.GetSubjectIdString();
        var favorite = string.IsNullOrEmpty(subject)
            ? []
            : (await favorites.GetFavoritesAsync(subject, cancellationToken)).ToList();
        var favoriteIds = favorite.Select(f => f.Id).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var food in favorite.Where(f => Matches(f, text) && !found.Any(x => x.Id == f.Id)))
            found.Add(food);

        var uses = await treatmentFoods.CountCarbIntakesByFoodAsync((time.GetUtcNow() - UsageWindow).UtcDateTime, cancellationToken);
        var results = found
            .Where(f => f.Id is not null && Guid.TryParse(f.Id, out _))
            .Select(f => new FoundFood(
                f.Id!,
                f.Name,
                string.IsNullOrWhiteSpace(f.Category) ? null : f.Category,
                string.IsNullOrWhiteSpace(f.Subcategory) ? null : f.Subcategory,
                favoriteIds.Contains(f.Id!),
                uses.GetValueOrDefault(Guid.Parse(f.Id!))))
            .OrderByDescending(f => f.CarbEntriesLast90Days)
            .ThenByDescending(f => f.Favorite)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxResults)
            .ToList();
        if (results.Count > 0)
            readLog.Record("foods", results.Count);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = Summarise(text, results, found.Count) }],
            StructuredContent = JsonSerializer.SerializeToElement(new { foods = results }, jsonOptions.Value.JsonSerializerOptions),
        };
    }

    private static bool Matches(Food food, string text) =>
        text.Length == 0
        || food.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
        || food.Category.Contains(text, StringComparison.OrdinalIgnoreCase)
        || food.Subcategory.Contains(text, StringComparison.OrdinalIgnoreCase);

    internal static string Summarise(string query, IReadOnlyList<FoundFood> results, int matched)
    {
        if (results.Count == 0)
            return query.Length == 0 ? "The food catalog is empty." : $"No food matches '{query}'.";

        var text = new StringBuilder();
        text.Append(query.Length == 0 ? "Foods, most used first" : $"Foods matching '{query}', most used first");
        text.Append(matched > results.Count ? $" ({results.Count} of {matched}):" : ":").Append('\n');
        foreach (var food in results)
        {
            text.Append(CultureInfo.InvariantCulture, $"- {food.Name} (id {food.Id}");
            if (food.Category is not null)
                text.Append(", category ").Append(food.Category);
            if (food.Favorite)
                text.Append(", favorite");
            text.Append(CultureInfo.InvariantCulture, $"): {food.CarbEntriesLast90Days} carb {(food.CarbEntriesLast90Days == 1 ? "entry" : "entries")} in the last 90 days.\n");
        }
        return text.ToString().TrimEnd();
    }
}

internal sealed record FoundFood(string Id, string Name, string? Category, string? Subcategory, bool Favorite, int CarbEntriesLast90Days);
