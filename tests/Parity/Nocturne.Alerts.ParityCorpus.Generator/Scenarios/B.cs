using System.Text.Json;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;

namespace Nocturne.Alerts.ParityCorpus.Generator.Scenarios;

/// <summary>
/// Terse builder helpers for corpus scenario authoring. Times are anchored at
/// <see cref="T0"/> (Monday 2026-01-05 12:00 UTC) so day-of-week / time-of-day scenarios
/// are deterministic and self-describing.
/// </summary>
public static class B
{
    /// <summary>Monday, 5 January 2026, 12:00:00 UTC.</summary>
    public static readonly DateTime T0 = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    public static DateTime T(int minutes) => T0.AddMinutes(minutes);

    public static Guid RId(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    public static JsonElement J(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static ScenarioFile Scenario(
        string name, string description, List<ScenarioRule> rules, List<ScenarioTick> ticks) =>
        new() { Name = name, Description = description, Rules = rules, Ticks = ticks };

    public static ScenarioRule Rule(
        int id,
        string type,
        string paramsJson,
        int confirm = 1,
        int hysteresis = 0,
        string? autoResolveParams = null,
        string? name = null) =>
        new()
        {
            Id = RId(id),
            Name = name ?? $"rule-{id}",
            ConditionType = type,
            ConditionParams = J(paramsJson),
            ConfirmationReadings = confirm,
            HysteresisMinutes = hysteresis,
            AutoResolveEnabled = autoResolveParams is not null,
            AutoResolveParams = autoResolveParams is null ? null : J(autoResolveParams),
        };

    public static ScenarioTick Tick(DateTime at, ScenarioContext ctx) =>
        new() { At = at, Context = ctx };

    /// <summary>
    /// Baseline context with a fresh reading at <paramref name="at"/>: glucose
    /// <paramref name="glucose"/> (default 100), flat trend. Override fields with
    /// <c>with</c>.
    /// </summary>
    public static ScenarioContext Ctx(DateTime at, decimal? glucose = 100m, decimal? trendRate = 0m) =>
        new()
        {
            LatestValue = glucose,
            LatestTimestamp = glucose is null ? null : at,
            TrendRate = trendRate,
            LastReadingAt = at,
        };

    /// <summary>A context with no data at all (cold start).</summary>
    public static ScenarioContext Empty() => new();

    /// <summary>Ticks every 5 minutes from <see cref="T0"/>, contexts produced per index.</summary>
    public static List<ScenarioTick> Every5(int count, Func<int, DateTime, ScenarioContext> ctx) =>
        Enumerable.Range(0, count)
            .Select(i => Tick(T(i * 5), ctx(i, T(i * 5))))
            .ToList();
}
