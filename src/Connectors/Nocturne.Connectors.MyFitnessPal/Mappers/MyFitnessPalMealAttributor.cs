using System.Globalization;
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
///     39. Anything the search cannot settle is reported as unattributed rather than guessed at.
/// </remarks>
public static class MyFitnessPalMealAttributor
{
    /// <summary>
    ///     Absolute tolerance per meal, in calories or grams. The two endpoints round
    ///     independently, so exact equality is too strict.
    /// </summary>
    private const decimal Tolerance = 0.75m;

    /// <summary>
    ///     Caps the search so a pathological day cannot stall a sync. Reaching it abandons the day.
    /// </summary>
    private const int MaxSteps = 2_000_000;

    /// <summary>
    ///     Days with more entries than this are not attributed; the search space grows as
    ///     meals^entries and a unique answer gets steadily less likely.
    /// </summary>
    private const int MaxEntries = 24;

    /// <summary>
    ///     One kilojoule in calories. The legacy diary reports energy in the account's own unit,
    ///     while the graph always reports calories.
    /// </summary>
    private const decimal CaloriesPerKilojoule = 1m / 4.184m;

    /// <summary>
    ///     Assigns meal names to a single day's entries.
    /// </summary>
    /// <param name="entries">
    ///     The complete set of the day's entries. A partial set cannot reconcile against the
    ///     day's meal totals and yields no attribution.
    /// </param>
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

        if (entries.Count == 0 || meals.Count == 0)
            return empty;

        // An unnamed meal row still accounts for part of the day, so its entries cannot be told
        // apart from the rest and the day as a whole is unsolvable. A named row with no totals is
        // treated as empty below, which is inert rather than fatal.
        if (meals.Any(m => string.IsNullOrEmpty(m.DiaryMeal)))
            return empty;

        // Entries with no energy and no protein — water, black coffee, diet drinks — fit every
        // meal equally and would make any day containing one ambiguous. They carry no carbs, so
        // leaving them unnamed costs the matching pipeline nothing.
        var solvable = entries
            .Where(e => Calories(e) != 0 || Protein(e) != 0)
            .ToList();

        // Applied after the filter: a long day of mostly water is still cheap to solve.
        if (solvable.Count == 0 || solvable.Count > MaxEntries)
            return empty;

        // Calories first: it is the dimension the two endpoints agree on most closely, so it
        // prunes hardest. Protein breaks the remaining ties.
        var itemCalories = solvable.Select(Calories).ToArray();
        var itemProtein = solvable.Select(Protein).ToArray();
        var mealCalories = meals.Select(m => ToCalories(m.NutritionalContents?.Energy)).ToArray();
        var mealProtein = meals.Select(m => m.NutritionalContents?.Protein ?? 0).ToArray();

        // The search prunes on partial sums exceeding a target, which is only sound while every
        // contribution is non-negative.
        if (itemCalories.Any(v => v < 0) || itemProtein.Any(v => v < 0)
            || mealCalories.Any(v => v < 0) || mealProtein.Any(v => v < 0))
            return empty;

        // A day that does not add up means the caller did not supply the whole day, or the two
        // sources disagree about which entries exist. Either way there is nothing to solve.
        if (Math.Abs(itemCalories.Sum() - mealCalories.Sum()) > Tolerance * meals.Count)
            return empty;

        // Entries are interchangeable only if everything the import carries matches, not merely
        // the two dimensions the search happens to use. Two different foods can share calories
        // and protein while differing in carbs, and carbs are what the matching pipeline reads.
        var interchangeable = solvable.Select(Signature).ToArray();

        var assignment = new PartitionSearch(
                itemCalories, itemProtein, mealCalories, mealProtein, interchangeable)
            .FindUniqueAssignment();
        if (assignment == null)
            return empty;

        return BuildResult(solvable, meals, assignment, interchangeable);
    }

    /// <summary>
    ///     Identifies entries whose imports would be identical, so that swapping which of them
    ///     went to which meal cannot change what is published.
    /// </summary>
    private static string Signature(MfpFoodDiaryEntryNode entry)
    {
        var n = entry.ConsumedNutrientSet;
        decimal[] values =
        [
            entry.Quantity,
            n?.Calories ?? 0,
            n?.Protein ?? 0,
            n?.Carbs ?? 0,
            n?.Fat ?? 0,
            entry.ServingSize?.Amount ?? 0,
        ];

        string[] parts =
        [
            entry.Food?.Id ?? string.Empty,
            entry.ServingSize?.Unit ?? string.Empty,
            entry.LoggedAt ?? string.Empty,
            // A fixed format: decimal keeps its scale, so 100 and 100.0 are equal numerically
            // but would otherwise render differently and split an interchangeable group.
            .. values.Select(v => v.ToString("0.############", CultureInfo.InvariantCulture)),
        ];

        return string.Join('|', parts);
    }

    /// <summary>
    ///     Emits the meal names, allocating each group of interchangeable entries by entry id so
    ///     that the same diary always produces the same answer regardless of search order.
    /// </summary>
    private static Dictionary<string, string> BuildResult(
        List<MfpFoodDiaryEntryNode> solvable,
        IReadOnlyList<MfpDiaryItem> meals,
        int[] assignment,
        string[] signatures)
    {
        var result = new Dictionary<string, string>(solvable.Count);

        foreach (var group in Enumerable.Range(0, solvable.Count).GroupBy(i => signatures[i]))
        {
            var indices = group.OrderBy(i => solvable[i].Id, StringComparer.Ordinal).ToList();
            var assigned = group.Select(i => assignment[i]).OrderBy(m => m).ToList();

            for (var k = 0; k < indices.Count; k++)
                result[solvable[indices[k]].Id] = meals[assigned[k]].DiaryMeal!;
        }

        return result;
    }

    private static decimal Calories(MfpFoodDiaryEntryNode entry) =>
        entry.ConsumedNutrientSet?.Calories ?? 0;

    private static decimal Protein(MfpFoodDiaryEntryNode entry) =>
        entry.ConsumedNutrientSet?.Protein ?? 0;

    /// <summary>
    ///     Normalises a legacy diary energy value to calories. Accounts configured in kilojoules
    ///     would otherwise report totals about 4.184x the graph's, and no day would ever reconcile.
    /// </summary>
    private static decimal ToCalories(MfpDiaryEnergy? energy)
    {
        if (energy == null)
            return 0;

        var unit = energy.Unit?.Trim();
        var isKilojoules = unit != null
            && (unit.StartsWith("kilojoule", StringComparison.OrdinalIgnoreCase)
                || unit.Equals("kj", StringComparison.OrdinalIgnoreCase));

        return isKilojoules
            ? energy.Value * CaloriesPerKilojoule
            : energy.Value;
    }

    /// <summary>
    ///     Depth-first search over entry-to-meal assignments, pruning any branch that overshoots a
    ///     meal's total.
    /// </summary>
    /// <remarks>
    ///     Two assignments that differ only in which of several identical entries went to which
    ///     meal describe the same outcome, so solutions are compared in a canonical form that
    ///     groups entries by their nutrient values. The search stops once two genuinely different
    ///     solutions are seen, and reports abandonment separately from failure so that hitting the
    ///     step cap is never mistaken for having proved a solution unique.
    /// </remarks>
    private sealed class PartitionSearch(
        decimal[] itemCalories,
        decimal[] itemProtein,
        decimal[] mealCalories,
        decimal[] mealProtein,
        string[] signatures)
    {
        private readonly int[] _assignment = new int[itemCalories.Length];
        private readonly decimal[] _calories = new decimal[mealCalories.Length];
        private readonly decimal[] _protein = new decimal[mealCalories.Length];

        /// <summary>Index of the first entry that would import identically to each entry.</summary>
        private readonly int[] _valueGroup = BuildValueGroups(signatures);

        private int[]? _firstSolution;
        private string? _firstCanonical;
        private bool _ambiguous;
        private bool _aborted;
        private int _steps;

        public int[]? FindUniqueAssignment()
        {
            Search(0);

            // Abandoning the search leaves the rest of the tree unexplored, so a solution found
            // before the cap has not been shown to be the only one.
            if (_aborted || _ambiguous)
                return null;

            return _firstSolution;
        }

        private static int[] BuildValueGroups(string[] signatures)
        {
            var groups = new int[signatures.Length];
            var first = new Dictionary<string, int>(signatures.Length, StringComparer.Ordinal);

            for (var i = 0; i < signatures.Length; i++)
            {
                if (first.TryGetValue(signatures[i], out var owner))
                {
                    groups[i] = owner;
                    continue;
                }

                groups[i] = i;
                first[signatures[i]] = i;
            }

            return groups;
        }

        /// <summary>
        ///     Renders an assignment so that swapping identical entries between meals compares equal.
        /// </summary>
        private string Canonical()
        {
            return string.Join(
                "|",
                _assignment
                    .Select((meal, i) => (Group: _valueGroup[i], Meal: meal))
                    .GroupBy(x => x.Group)
                    .OrderBy(g => g.Key)
                    .Select(g => $"{g.Key}:{string.Join(",", g.Select(x => x.Meal).OrderBy(m => m))}"));
        }

        private void Search(int index)
        {
            if (_ambiguous || _aborted)
                return;

            if (++_steps > MaxSteps)
            {
                _aborted = true;
                return;
            }

            if (index == itemCalories.Length)
            {
                for (var m = 0; m < mealCalories.Length; m++)
                    if (Math.Abs(_calories[m] - mealCalories[m]) > Tolerance
                        || Math.Abs(_protein[m] - mealProtein[m]) > Tolerance)
                        return;

                var canonical = Canonical();
                if (_firstSolution == null)
                {
                    _firstSolution = (int[])_assignment.Clone();
                    _firstCanonical = canonical;
                }
                else if (canonical != _firstCanonical)
                {
                    _ambiguous = true;
                }

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

                if (_ambiguous || _aborted)
                    return;
            }
        }
    }
}
