using Nocturne.Core.Models;

namespace Nocturne.Services.Demo.Services;

/// <summary>
/// Deterministic lifestyle sample data: the food library with per-meal
/// attribution, body-weight trend, state spans (pump mode, profile, overrides,
/// exercise, illness, travel), the timezone-timeline trip, and the default
/// clock face. All keyed off <see cref="DayScenarios"/> so every stream tells
/// the same story as the glucose it sits beside.
/// </summary>
public static class DemoLifestyleSeeds
{
    /// <summary>A food library item; carbs/fat/protein per portion in grams.</summary>
    public sealed record FoodSeed(
        string Name, string Category, string Subcategory,
        double Portion, string Unit, double Carbs, double Protein, double Fat, double Energy);

    /// <summary>Foods referenced by generated meals, plus a few browsables.</summary>
    public static readonly IReadOnlyList<FoodSeed> FoodLibrary =
    [
        new("Porridge with berries", "Breakfast", "Cereal", 250, "g", 42, 8, 6, 260),
        new("Wholegrain toast with eggs", "Breakfast", "Bread", 160, "g", 28, 16, 14, 300),
        new("Greek yoghurt with granola", "Breakfast", "Dairy", 200, "g", 34, 12, 9, 280),
        new("Chicken salad wrap", "Lunch", "Wrap", 220, "g", 38, 24, 11, 350),
        new("Leftover pasta bake", "Lunch", "Pasta", 300, "g", 52, 18, 13, 400),
        new("Sushi set", "Lunch", "Rice", 250, "g", 55, 15, 5, 330),
        new("Beef stir-fry with rice", "Dinner", "Rice", 350, "g", 58, 28, 15, 480),
        new("Roast chicken with potatoes", "Dinner", "Roast", 380, "g", 45, 32, 18, 470),
        new("Homemade pizza slices", "Dinner", "Pizza", 280, "g", 62, 22, 20, 520),
        new("Lentil curry with naan", "Dinner", "Curry", 400, "g", 60, 20, 12, 450),
        new("Apple", "Snack", "Fruit", 150, "g", 16, 0, 0, 62),
        new("Muesli bar", "Snack", "Bar", 35, "g", 18, 3, 5, 130),
        new("Crackers and cheese", "Snack", "Crackers", 60, "g", 15, 7, 9, 170),
        new("Banana", "Snack", "Fruit", 120, "g", 20, 1, 0, 80),
        new("Jelly beans", "Snack", "Hypo treatment", 30, "g", 25, 0, 0, 100),
    ];

    /// <summary>
    /// The library foods plausibly making up a generated meal, deterministic per
    /// date. Meal names come from the generator's meal plan ("Breakfast",
    /// "Lunch", "Dinner", "Snack").
    /// </summary>
    public static IReadOnlyList<FoodSeed> MealFoodsFor(DateTime localDay, string mealName)
    {
        var candidates = FoodLibrary.Where(f => f.Category == mealName).ToList();
        if (candidates.Count == 0)
            return [];

        var rng = DayScenarios.RngFor(localDay, $"food:{mealName}");
        return [candidates[rng.Next(candidates.Count)]];
    }

    /// <summary>Weekly weigh-in weight: a slow seasonal drift with wobble, deterministic per date.</summary>
    public static double WeightKgOn(DateTime localDay)
    {
        var dayNumber = (int)(localDay.Date.Ticks / TimeSpan.TicksPerDay);
        var drift = 1.2 * Math.Sin(dayNumber / 120.0 * 2 * Math.PI);
        var wobble = (DayScenarios.Roll(localDay, "weight", 13) - 6) * 0.1;
        return Math.Round(77.5 + drift + wobble, 1);
    }

    /// <summary>
    /// One state span to seed; local times, null end = still active. Metadata
    /// follows the decomposer's conventions (e.g. Profile spans carry the name
    /// in <c>profileName</c>, temporary targets carry <c>targetTop/Bottom</c>)
    /// so the resolvers that read spans see the same shape as real uploads.
    /// </summary>
    public sealed record SpanSeed(
        StateSpanCategory Category,
        string State,
        DateTime StartLocal,
        DateTime? EndLocal,
        IReadOnlyDictionary<string, object>? Metadata = null);

    /// <summary>The timezone-timeline trip: NYC for five days, three weeks back.</summary>
    public const string HomeTimezoneFallback = "Australia/Sydney";
    public const string TripTimezone = "America/New_York";
    public const int TripStartDaysAgo = 21;
    public const int TripLengthDays = 5;

    /// <summary>
    /// State spans for the backfill window: pump mode (Automatic, with weekly
    /// Manual windows and Exercise mode during workouts), the active profile,
    /// workout overrides + temporary targets, illness spans on sick days, and
    /// the travel span matching the timezone-timeline trip.
    /// </summary>
    public static List<SpanSeed> BuildSpans(DateTime localToday, int days)
    {
        var spans = new List<SpanSeed>();
        var windowStart = localToday.AddDays(-days);
        var now = DateTime.Now;

        // Profile: the seeded therapy profile is active for the whole window.
        // Resolvers read the name from metadata, not from State (which is the
        // ProfileState enum name).
        spans.Add(new SpanSeed(
            StateSpanCategory.Profile, nameof(ProfileState.Active), windowStart, null,
            new Dictionary<string, object> { ["profileName"] = DemoTherapyProfile.ProfileName }));

        // Pump mode: Automatic, interrupted by occasional Manual windows and
        // by Exercise mode during workouts on exercise days.
        var interruptions = new List<(DateTime Start, DateTime End, string State)>();
        for (var d = 0; d <= days; d++)
        {
            var day = windowStart.AddDays(d);
            var scenario = DayScenarios.For(day);

            if (scenario == DayScenario.Exercise)
            {
                var (workoutStart, workoutEnd) = DemoHealthDataGenerator.WorkoutWindowFor(day);
                interruptions.Add((workoutStart.AddMinutes(-10), workoutEnd.AddMinutes(10), nameof(PumpModeState.Exercise)));

                spans.Add(new SpanSeed(StateSpanCategory.Override, "Workout Mode",
                    workoutStart.AddMinutes(-10), workoutEnd.AddMinutes(10)));
                spans.Add(new SpanSeed(
                    StateSpanCategory.TemporaryTarget, nameof(TemporaryTargetState.Active),
                    workoutStart.AddMinutes(-10), workoutEnd.AddMinutes(10),
                    new Dictionary<string, object> { ["targetTop"] = 150.0, ["targetBottom"] = 140.0 }));
            }
            else if (DayScenarios.Roll(day, "manual-mode", 100) < 9)
            {
                // An occasional afternoon in manual mode (pump maintenance, pool day).
                var start = day.AddHours(13 + DayScenarios.Roll(day, "manual-start", 4));
                interruptions.Add((start, start.AddHours(2.5), nameof(PumpModeState.Manual)));
            }
        }

        var cursor = windowStart;
        foreach (var (start, end, state) in interruptions.Where(i => i.Start > windowStart).OrderBy(i => i.Start))
        {
            if (start > cursor)
                spans.Add(new SpanSeed(StateSpanCategory.PumpMode, nameof(PumpModeState.Automatic), cursor, start));
            spans.Add(new SpanSeed(StateSpanCategory.PumpMode, state, start, end));
            cursor = end;
        }
        spans.Add(new SpanSeed(StateSpanCategory.PumpMode, nameof(PumpModeState.Automatic), cursor, null));

        // Illness: contiguous sick-day runs become one span.
        DateTime? sickStart = null;
        for (var d = 0; d <= days + 1; d++)
        {
            var day = windowStart.AddDays(d);
            var isSick = d <= days && DayScenarios.For(day) == DayScenario.SickDay;
            if (isSick && sickStart is null)
                sickStart = day.AddHours(6);
            else if (!isSick && sickStart is not null)
            {
                spans.Add(new SpanSeed(StateSpanCategory.Illness, "Sick day", sickStart.Value, day.AddHours(-2)));
                sickStart = null;
            }
        }

        // Travel: matches the timezone-timeline trip.
        var tripStart = localToday.AddDays(-TripStartDaysAgo).AddHours(14);
        var tripEnd = tripStart.AddDays(TripLengthDays).AddHours(-4);
        if (tripStart > windowStart)
            spans.Add(new SpanSeed(StateSpanCategory.Travel, TripTimezone, tripStart, tripEnd));

        // Clamp everything to the past; drop spans that never started.
        return spans
            .Where(s => s.StartLocal < now)
            .Select(s => s.EndLocal is { } end && end > now ? s with { EndLocal = null } : s)
            .ToList();
    }

    /// <summary>
    /// Default clock-face config matching the web app's starter layout: big
    /// glucose + trend arrow on top, delta and time-ago beneath.
    /// </summary>
    public const string DefaultClockFaceConfigJson = """
        {
          "rows": [
            {
              "elements": [
                { "type": "sg", "size": 40, "style": { "color": "dynamic", "font": "system", "fontWeight": "medium", "opacity": 1.0 } },
                { "type": "arrow", "size": 25, "style": { "color": "dynamic", "font": "system", "fontWeight": "medium", "opacity": 1.0 } }
              ]
            },
            {
              "elements": [
                { "type": "delta", "size": 14, "style": { "color": "muted", "font": "system", "fontWeight": "regular", "opacity": 0.9 } },
                { "type": "ago", "size": 14, "style": { "color": "muted", "font": "system", "fontWeight": "regular", "opacity": 0.9 } }
              ]
            }
          ]
        }
        """;
}
