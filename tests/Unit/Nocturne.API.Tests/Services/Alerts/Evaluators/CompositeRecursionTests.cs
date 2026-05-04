using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

/// <summary>
/// Verifies that the composite/not/sustained evaluators correctly route every
/// <see cref="AlertConditionType"/> kind through <see cref="ConditionNodePayloads"/>.
/// Catches drift if a new kind is added without updating the helper.
/// </summary>
[Trait("Category", "Unit")]
public class CompositeRecursionTests
{
    private static readonly DateTime FixedNow = new(2026, 4, 30, 23, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions SnakeCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly FakeTimeProvider _timeProvider;
    private readonly StubTimerStore _timerStore;
    private readonly CompositeEvaluator _composite;

    public CompositeRecursionTests()
    {
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(FixedNow));
        _timerStore = new StubTimerStore();

        var serviceProvider = new Mock<IServiceProvider>();

        // Three recursive evaluators share the same provider so they can resolve the
        // registry that contains them.
        var compositeEval = new CompositeEvaluator(serviceProvider.Object);
        var notEval = new NotEvaluator(serviceProvider.Object);
        var sustainedEval = new SustainedEvaluator(serviceProvider.Object, _timerStore, _timeProvider);

        var evaluators = new IConditionEvaluator[]
        {
            new ThresholdEvaluator(),
            new RateOfChangeEvaluator(),
            new StalenessEvaluator(_timeProvider),
            new PredictedEvaluator(),
            new TrendEvaluator(),
            new TimeOfDayEvaluator(_timeProvider),
            new IobEvaluator(),
            new CobEvaluator(),
            new ReservoirEvaluator(),
            new SiteAgeEvaluator(_timeProvider),
            new SensorAgeEvaluator(_timeProvider),
            new AlertStateEvaluator(_timeProvider),
            compositeEval,
            notEval,
            sustainedEval,
        };

        var registry = new ConditionEvaluatorRegistry(evaluators);
        serviceProvider
            .Setup(sp => sp.GetService(typeof(ConditionEvaluatorRegistry)))
            .Returns(registry);

        _composite = compositeEval;
    }

    // ------------------------------------------------------------------
    // Bracketing test: (time AND sustained) OR predicted
    // Truth table cells are pinned in the test names to make a regression
    // immediately readable.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Brackets_LeftTrue_PredictedAny_ReturnsTrue()
    {
        // time=true (FixedNow 23:00 UTC is inside [22:00, 06:00) wrap window),
        // sustained=true (timer pre-elapsed at the path the inner composite threads),
        // predicted=false (irrelevant — left side is true).
        // Outer composite is the root (path=""), child[0]=composite (path "[0].composite"),
        // its child[1]=sustained (path "[0].composite[1].sustained").
        _timerStore.Set(_ruleId, "[0].composite[1].sustained", FixedNow.AddMinutes(-15));

        var json = SerializeBracketTree();
        var ctx = MakeContext(latestValue: 60m, predictions: Array.Empty<PredictedGlucosePoint>());

        (await _composite.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Brackets_LeftFalseSustained_PredictedTrue_ReturnsTrue()
    {
        // time=true, sustained=false (no timer recorded yet), predicted=true => OR right wins.
        var json = SerializeBracketTree();
        var ctx = MakeContext(
            latestValue: 60m,
            predictions: new[] { new PredictedGlucosePoint(15, 220m) });

        (await _composite.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Brackets_LeftFalseSustained_PredictedFalse_ReturnsFalse()
    {
        // time=true, sustained=false, predicted=false => both sides false.
        var json = SerializeBracketTree();
        var ctx = MakeContext(
            latestValue: 60m,
            predictions: new[] { new PredictedGlucosePoint(15, 150m) });

        (await _composite.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Brackets_LeftFalseTime_PredictedFalse_ReturnsFalse()
    {
        // Use a non-overlapping window so time=false at FixedNow (23:00 UTC). Left-side AND
        // short-circuits to false; predicted=false => both sides false.
        var tree = new CompositeCondition("or", new List<ConditionNode>
        {
            new("composite", Composite: new CompositeCondition("and", new List<ConditionNode>
            {
                // 23:00 UTC is OUTSIDE [06:00, 22:00).
                new("time_of_day", TimeOfDay: new TimeOfDayCondition("06:00", "22:00", "UTC")),
                new("sustained", Sustained: new SustainedCondition(
                    Minutes: 10,
                    Child: new ConditionNode("threshold", Threshold: new ThresholdCondition("below", 70)))),
            })),
            new("predicted", Predicted: new PredictedCondition(">", 200, 30)),
        });
        var json = JsonSerializer.Serialize(tree, SnakeCaseOptions);
        var ctx = MakeContext(
            latestValue: 60m,
            predictions: new[] { new PredictedGlucosePoint(15, 150m) });

        (await _composite.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeFalse();
    }

    private static string SerializeBracketTree()
    {
        var tree = new CompositeCondition("or", new List<ConditionNode>
        {
            new("composite", Composite: new CompositeCondition("and", new List<ConditionNode>
            {
                new("time_of_day", TimeOfDay: new TimeOfDayCondition("22:00", "06:00", "UTC")),
                new("sustained", Sustained: new SustainedCondition(
                    Minutes: 10,
                    Child: new ConditionNode("threshold", Threshold: new ThresholdCondition("below", 70)))),
            })),
            new("predicted", Predicted: new PredictedCondition(">", 200, 30)),
        });
        return JsonSerializer.Serialize(tree, SnakeCaseOptions);
    }

    // ------------------------------------------------------------------
    // Routes-all-kinds: prove the helper switch routes each previously-missing
    // kind correctly. Build a single-child OR composite and verify it returns
    // true when the leaf would return true. Before Task 15 these all silently
    // serialised "{}" and returned false.
    // ------------------------------------------------------------------

    [Fact]
    public async Task RoutesPredicted()
    {
        var json = SingleChildOr(new ConditionNode("predicted",
            Predicted: new PredictedCondition(">", 200m, 30)));
        var ctx = MakeContext(predictions: new[] { new PredictedGlucosePoint(15, 220m) });

        (await _composite.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task RoutesTrend()
    {
        var json = SingleChildOr(new ConditionNode("trend", Trend: new TrendCondition("rising_fast")));
        var ctx = MakeContext(trendBucket: TrendBucket.RisingFast);

        (await _composite.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task RoutesTimeOfDay()
    {
        // FixedNow is 23:00 UTC; window 22:00–06:00 wraps midnight.
        var json = SingleChildOr(new ConditionNode("time_of_day",
            TimeOfDay: new TimeOfDayCondition("22:00", "06:00", "UTC")));
        var ctx = MakeContext();

        (await _composite.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task RoutesIob()
    {
        var json = SingleChildOr(new ConditionNode("iob", Iob: new IobCondition(">", 2m)));
        var ctx = MakeContext(iob: 5m);

        (await _composite.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task RoutesCob()
    {
        var json = SingleChildOr(new ConditionNode("cob", Cob: new CobCondition(">", 10m)));
        var ctx = MakeContext(cob: 30m);

        (await _composite.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task RoutesReservoir()
    {
        var json = SingleChildOr(new ConditionNode("reservoir", Reservoir: new ReservoirCondition("<", 20m)));
        var ctx = MakeContext(reservoir: 5m);

        (await _composite.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task RoutesSiteAge()
    {
        // Site changed 4 days ago; condition fires if site age >= 72 hours.
        var json = SingleChildOr(new ConditionNode("site_age", SiteAge: new SiteAgeCondition(">=", 72m)));
        var ctx = MakeContext(lastSiteChangeAt: FixedNow.AddDays(-4));

        (await _composite.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task RoutesSensorAge()
    {
        var json = SingleChildOr(new ConditionNode("sensor_age", SensorAge: new SensorAgeCondition(">=", 10m)));
        var ctx = MakeContext(lastSensorStartAt: FixedNow.AddDays(-12));

        (await _composite.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task RoutesAlertState()
    {
        var alertId = Guid.NewGuid();
        var snapshot = new ActiveAlertSnapshot("firing", FixedNow.AddMinutes(-10), null);
        var json = SingleChildOr(new ConditionNode("alert_state",
            AlertState: new AlertStateCondition(alertId, "firing", null)));
        var ctx = MakeContext(activeAlerts: new Dictionary<Guid, ActiveAlertSnapshot> { [alertId] = snapshot });

        (await _composite.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    private static string SingleChildOr(ConditionNode child)
    {
        var composite = new CompositeCondition("or", new List<ConditionNode> { child });
        return JsonSerializer.Serialize(composite, SnakeCaseOptions);
    }

    // ------------------------------------------------------------------

    private readonly Guid _ruleId = Guid.NewGuid();

    private SensorContext MakeContext(
        decimal? latestValue = 100m,
        decimal? trendRate = 0m,
        TrendBucket? trendBucket = null,
        decimal? iob = null,
        decimal? cob = null,
        decimal? reservoir = null,
        DateTime? lastSiteChangeAt = null,
        DateTime? lastSensorStartAt = null,
        IReadOnlyList<PredictedGlucosePoint>? predictions = null,
        IReadOnlyDictionary<Guid, ActiveAlertSnapshot>? activeAlerts = null) => new()
    {
        LatestValue = latestValue,
        LatestTimestamp = FixedNow,
        TrendRate = trendRate,
        LastReadingAt = FixedNow,
        TrendBucket = trendBucket,
        IobUnits = iob,
        CobGrams = cob,
        ReservoirUnits = reservoir,
        LastSiteChangeAt = lastSiteChangeAt,
        LastSensorStartAt = lastSensorStartAt,
        Predictions = predictions ?? Array.Empty<PredictedGlucosePoint>(),
        ActiveAlerts = activeAlerts ?? new Dictionary<Guid, ActiveAlertSnapshot>(),
        CurrentRuleId = _ruleId,
        CurrentPath = string.Empty,
    };

    /// <summary>
    /// Minimal in-memory <see cref="IConditionTimerStore"/> so we can pre-seed timers
    /// for the bracketing test without standing up a DbContext.
    /// </summary>
    private sealed class StubTimerStore : IConditionTimerStore
    {
        private readonly Dictionary<(Guid, string), DateTime> _store = new();

        public void Set(Guid ruleId, string path, DateTime at) => _store[(ruleId, path)] = at;

        public Task<DateTime?> GetFirstTrueAsync(Guid ruleId, string path, CancellationToken ct) =>
            Task.FromResult(_store.TryGetValue((ruleId, path), out var v) ? v : (DateTime?)null);

        public Task SetFirstTrueAsync(Guid ruleId, string path, DateTime at, CancellationToken ct)
        {
            _store[(ruleId, path)] = at;
            return Task.CompletedTask;
        }

        public Task ClearAsync(Guid ruleId, string path, CancellationToken ct)
        {
            _store.Remove((ruleId, path));
            return Task.CompletedTask;
        }

        public Task ClearAllForRuleAsync(Guid ruleId, CancellationToken ct)
        {
            foreach (var key in _store.Keys.Where(k => k.Item1 == ruleId).ToList())
                _store.Remove(key);
            return Task.CompletedTask;
        }

        public Task PruneToPathsAsync(Guid ruleId, IReadOnlyCollection<string> retainedPaths, CancellationToken ct)
        {
            var keep = new HashSet<string>(retainedPaths);
            foreach (var key in _store.Keys.Where(k => k.Item1 == ruleId && !keep.Contains(k.Item2)).ToList())
                _store.Remove(key);
            return Task.CompletedTask;
        }
    }
}
