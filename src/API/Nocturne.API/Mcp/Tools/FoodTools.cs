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

    /// <summary>How far back a history-clamped token's uses are counted: all it can read.</summary>
    internal static readonly TimeSpan ClampedWindow = TimeSpan.FromHours(24);

    [McpServerTool(Name = "find_foods", Title = "Find foods", ReadOnly = true, OpenWorld = false, Idempotent = true)]
    [Description(
        "Searches the food catalog and the user's favorite foods by name, category or subcategory, and returns each "
        + "food's id, name and category and whether it is a favorite. With treatments.read it also counts the carb entries "
        + "of the last 90 days (the last 24 hours for a token limited to them) that carry a food record of it. Use the ids "
        + "or the category in an event_response meal cohort's 'foods' or 'foodCategory'. Meals logged as carbs only carry "
        + "no food record and cannot match a food filter. At most 25 foods, most used first where counted.")]
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

        var http = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("find_foods ran outside an HTTP request.");
        var found = (await foods.SearchFoodAsync(text, 200, 0, cancellationToken)).ToList();
        var subject = http.GetSubjectIdString();
        var favorite = string.IsNullOrEmpty(subject)
            ? []
            : (await favorites.GetFavoritesAsync(subject, cancellationToken)).ToList();
        var favoriteIds = favorite.Select(f => f.Id).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var food in favorite.Where(f => Matches(f, text) && !found.Any(x => x.Id == f.Id)))
            found.Add(food);

        // The counts are of carb entries, which are treatments: a food.read-only token gets none.
        // A clamped token's reads stop 24 hours back, so its counts cover only that window.
        var window = !http.HasScope(Scope.TreatmentsRead) ? (TimeSpan?)null
            : http.IsCallerHistoryClamped() ? ClampedWindow : UsageWindow;
        var uses = window is { } span
            ? await treatmentFoods.CountCarbIntakesByFoodAsync((time.GetUtcNow() - span).UtcDateTime, cancellationToken)
            : null;
        var results = found
            .Where(f => f.Id is not null && Guid.TryParse(f.Id, out _))
            .Select(f => new FoundFood(
                f.Id!,
                f.Name,
                string.IsNullOrWhiteSpace(f.Category) ? null : f.Category,
                string.IsNullOrWhiteSpace(f.Subcategory) ? null : f.Subcategory,
                favoriteIds.Contains(f.Id!),
                uses?.GetValueOrDefault(Guid.Parse(f.Id!))))
            .OrderByDescending(f => f.CarbEntries ?? 0)
            .ThenByDescending(f => f.Favorite)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxResults)
            .ToList();
        if (results.Count > 0)
            readLog.Record("foods", results.Count);
        if (uses is not null)
            readLog.Record("treatments", uses.Values.Sum());

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = Summarise(text, results, found.Count, window) }],
            StructuredContent = JsonSerializer.SerializeToElement(
                new { foods = results, carbEntriesWindowHours = window?.TotalHours },
                jsonOptions.Value.JsonSerializerOptions),
        };
    }

    private static bool Matches(Food food, string text) =>
        text.Length == 0
        || food.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
        || food.Category.Contains(text, StringComparison.OrdinalIgnoreCase)
        || food.Subcategory.Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <param name="window">How far back the carb entries were counted; null when they were not.</param>
    internal static string Summarise(string query, IReadOnlyList<FoundFood> results, int matched, TimeSpan? window)
    {
        if (results.Count == 0)
            return query.Length == 0 ? "The food catalog is empty." : $"No food matches '{query}'.";

        var counted = window is not null;
        var period = window == ClampedWindow ? "the last 24 hours" : "the last 90 days";
        var text = new StringBuilder();
        text.Append(query.Length == 0 ? "Foods" : $"Foods matching '{query}'");
        text.Append(counted ? ", most used first" : ", by name");
        text.Append(matched > results.Count ? $" ({results.Count} of {matched}):" : ":").Append('\n');
        if (!counted)
            text.Append("Carb entries are not counted: they need the treatments.read scope, which this token does not hold.\n");
        else if (window == ClampedWindow)
            text.Append("Carb entries are counted over the last 24 hours only: this token does not read older data.\n");
        foreach (var food in results)
        {
            text.Append(CultureInfo.InvariantCulture, $"- {food.Name} (id {food.Id}");
            if (food.Category is not null)
                text.Append(", category ").Append(food.Category);
            if (food.Favorite)
                text.Append(", favorite");
            text.Append(')');
            if (food.CarbEntries is { } n)
                text.Append(CultureInfo.InvariantCulture, $": {n} carb {(n == 1 ? "entry" : "entries")} in {period}");
            text.Append(".\n");
        }
        return text.ToString().TrimEnd();
    }
}

/// <param name="CarbEntries">Carb entries in the counted window with a food record of it; null when not counted.</param>
internal sealed record FoundFood(string Id, string Name, string? Category, string? Subcategory, bool Favorite, int? CarbEntries);
