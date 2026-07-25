using Nocturne.Connectors.MyFitnessPal.Models;

namespace Nocturne.Connectors.MyFitnessPal.Mappers;

/// <summary>
///     Works out which meal each food diary entry belongs to.
/// </summary>
/// <remarks>
///     Production exposes the two halves of a day separately and with no shared identifier: the
///     GraphQL sync returns itemised entries with no meal, and the legacy diary returns per-meal
///     totals with no items. The entries of a day do however partition exactly into those totals,
///     so the assignment is recovered by searching for a partition whose per-meal sums match.
///     Matching on calories alone left 1 of 39 sampled days ambiguous; adding protein resolved all
///     39. A day that stays ambiguous, or does not add up at all, is reported as unattributed
///     rather than guessed at.
/// </remarks>
public static class MyFitnessPalMealAttributor
{
    /// <summary>
    ///     Absolute tolerance per meal, in calories or grams. The two endpoints round
    ///     independently, so exact equality is too strict.
    /// </summary>
    private const decimal Tolerance = 0.75m;

    /// <summary>
    ///     Caps the search so a pathological day cannot stall a sync.
    /// </summary>
    private const int MaxSteps = 2_000_000;

    /// <summary>
    ///     Days with more entries than this are not attributed; the search space grows as
    ///     meals^entries and a unique answer gets steadily less likely.
    /// </summary>
    private const int MaxEntries = 24;

    /// <summary>
    ///     Assigns meal names to a single day's entries.
    /// </summary>
    /// <param name="entries">The day's entries, as returned by the GraphQL sync.</param>
    /// <param name="meals">The same day's meal totals from the legacy diary.</param>
    /// <returns>
    ///     Entry id to meal name. Empty when no single partition explains the day, in which case
    ///     the caller should import the entries without a meal name.
    /// </returns>
    public static IReadOnlyDictionary<string, string> Attribute(
        IReadOnlyList<MfpFoodDiaryEntryNode> entries,
        IReadOnlyList<MfpDiaryItem> meals)
    {
        var empty = new Dictionary<string, string>();

        if (entries.Count == 0 || entries.Count > MaxEntries)
            return empty;

        var named = meals
            .Where(m => !string.IsNullOrEmpty(m.DiaryMeal) && m.NutritionalContents != null)
            .ToList();
        if (named.Count == 0)
            return empty;

        // Calories first: it is the dimension the two endpoints agree on most closely, so it
        // prunes hardest. Protein breaks the remaining ties.
        var itemCalories = entries.Select(e => e.ConsumedNutrientSet?.Calories ?? 0).ToArray();
        var itemProtein = entries.Select(e => e.ConsumedNutrientSet?.Protein ?? 0).ToArray();
        var mealCalories = named.Select(m => m.NutritionalContents!.Energy?.Value ?? 0).ToArray();
        var mealProtein = named.Select(m => m.NutritionalContents!.Protein ?? 0).ToArray();

        // A day that does not add up means the two sources disagree about which entries exist,
        // so there is nothing to solve.
        if (Math.Abs(itemCalories.Sum() - mealCalories.Sum()) > Tolerance * named.Count)
            return empty;

        var solver = new PartitionSearch(itemCalories, itemProtein, mealCalories, mealProtein);
        var assignment = solver.FindUniqueAssignment();
        if (assignment == null)
            return empty;

        var result = new Dictionary<string, string>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
            result[entries[i].Id] = named[assignment[i]].DiaryMeal!;

        return result;
    }

    /// <summary>
    ///     Depth-first search over entry-to-meal assignments, pruning any branch that overshoots a
    ///     meal's total. Stops as soon as a second solution is found, since that makes the day
    ///     ambiguous and the result unusable either way.
    /// </summary>
    private sealed class PartitionSearch(
        decimal[] itemCalories,
        decimal[] itemProtein,
        decimal[] mealCalories,
        decimal[] mealProtein)
    {
        private readonly int[] _assignment = new int[itemCalories.Length];
        private readonly decimal[] _calories = new decimal[mealCalories.Length];
        private readonly decimal[] _protein = new decimal[mealCalories.Length];

        private int[]? _firstSolution;
        private int _solutions;
        private int _steps;

        public int[]? FindUniqueAssignment()
        {
            Search(0);
            return _solutions == 1 ? _firstSolution : null;
        }

        private void Search(int index)
        {
            if (_solutions > 1 || ++_steps > MaxSteps)
                return;

            if (index == itemCalories.Length)
            {
                for (var m = 0; m < mealCalories.Length; m++)
                    if (Math.Abs(_calories[m] - mealCalories[m]) > Tolerance
                        || Math.Abs(_protein[m] - mealProtein[m]) > Tolerance)
                        return;

                _solutions++;
                _firstSolution ??= (int[])_assignment.Clone();
                return;
            }

            for (var m = 0; m < mealCalories.Length; m++)
            {
                if (_calories[m] + itemCalories[index] > mealCalories[m] + Tolerance)
                    continue;
                if (_protein[m] + itemProtein[index] > mealProtein[m] + Tolerance)
                    continue;

                _calories[m] += itemCalories[index];
                _protein[m] += itemProtein[index];
                _assignment[index] = m;

                Search(index + 1);

                _calories[m] -= itemCalories[index];
                _protein[m] -= itemProtein[index];

                if (_solutions > 1 || _steps > MaxSteps)
                    return;
            }
        }
    }
}
