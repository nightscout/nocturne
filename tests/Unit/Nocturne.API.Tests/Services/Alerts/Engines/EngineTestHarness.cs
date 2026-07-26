using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// Availability gate for the nocturne_alerts native library, mirroring the gate in
/// Nocturne.Alerts.Native.Tests: rust-engine tests skip cleanly when the cargo-built
/// library is absent, and <c>NOCTURNE_ALERTS_REQUIRE_NATIVE=1</c> (set by the
/// alerts-ffi-parity CI job) turns silent skips into hard failures via
/// <see cref="EngineNativeGateTests"/>.
/// </summary>
internal static class EngineNativeGate
{
    public const string SkipReason =
        "nocturne_alerts native library not found - build it with 'cargo build --release -p nocturne-alerts-ffi' " +
        "from crates/ (then rebuild this test project) or point NOCTURNE_ALERTS_NATIVE_DIR at the directory containing it.";

    public static readonly bool IsAvailable = Probe();

    private static bool Probe()
    {
        try
        {
            return AlertsInterop.IsAvailable();
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>A fact that is skipped when the native library is unavailable.</summary>
public sealed class EngineNativeFactAttribute : FactAttribute
{
    public EngineNativeFactAttribute()
    {
        if (!EngineNativeGate.IsAvailable)
            Skip = EngineNativeGate.SkipReason;
    }
}

/// <summary>A theory that is skipped when the native library is unavailable.</summary>
public sealed class EngineNativeTheoryAttribute : TheoryAttribute
{
    public EngineNativeTheoryAttribute()
    {
        if (!EngineNativeGate.IsAvailable)
            Skip = EngineNativeGate.SkipReason;
    }
}

public class EngineNativeGateTests
{
    [Fact]
    public void Native_library_is_present_when_required()
    {
        if (Environment.GetEnvironmentVariable("NOCTURNE_ALERTS_REQUIRE_NATIVE") == "1" && !EngineNativeGate.IsAvailable)
        {
            Assert.Fail(
                "NOCTURNE_ALERTS_REQUIRE_NATIVE=1 but the nocturne_alerts native library could not be loaded. " +
                EngineNativeGate.SkipReason);
        }
    }
}

/// <summary>
/// Shared fixtures for the engine-seam tests: corpus location, scenario-to-domain
/// conversions (mirroring the generator's private helpers), engine factories over the
/// generator's in-memory fakes, and a JSON deep-diff for corpus comparison.
/// </summary>
internal static class EngineTestHarness
{
    public static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0002-000000000001");

    private static readonly JsonSerializerOptions EnumWireOptions = BuildEnumWireOptions();

    private static JsonSerializerOptions BuildEnumWireOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter<TrendBucket>());
        return options;
    }

    // -----------------------------------------------------------------------
    // Corpus access
    // -----------------------------------------------------------------------

    public static string CorpusDirectory() =>
        Path.Combine(FindRepoRoot(), "tests", "Parity", "AlertEngineCorpus");

    public static IReadOnlyList<string> EnumerateScenarioNames() =>
        Directory.EnumerateFiles(CorpusDirectory(), "*.json")
            .Where(p => !p.EndsWith(".expected.json", StringComparison.Ordinal))
            .Select(p => Path.GetFileNameWithoutExtension(p)!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    public static async Task<(ScenarioFile Scenario, JsonNode Expected)> LoadScenarioAsync(string name)
    {
        var dir = CorpusDirectory();
        var scenarioJson = await File.ReadAllTextAsync(Path.Combine(dir, $"{name}.json"));
        var expectedJson = await File.ReadAllTextAsync(Path.Combine(dir, $"{name}.expected.json"));
        var scenario = JsonSerializer.Deserialize<ScenarioFile>(scenarioJson, CorpusJson.Options)
            ?? throw new InvalidOperationException($"Failed to parse scenario '{name}'");
        return (scenario, JsonNode.Parse(expectedJson)!);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nocturne.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the repo root (nocturne.sln) above " + AppContext.BaseDirectory);
    }

    // -----------------------------------------------------------------------
    // Engine factories (over the generator's in-memory fakes)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a <see cref="ManagedAlertEngine"/> with the evaluator registry sourced from
    /// the single AddAlertEvaluators registration (so the seam test can never drift behind
    /// the live engine) and the supplied in-memory state fakes. The returned provider owns
    /// the evaluator lifetimes; dispose it with the test.
    /// </summary>
    public static (ManagedAlertEngine Engine, ServiceProvider Provider) BuildManagedEngine(
        ManualTimeProvider time,
        IConditionTimerStore timerStore,
        InMemoryTrackerRepository trackerRepo)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(timerStore);
        services.AddAlertEvaluators();
        services.AddSingleton<ConditionEvaluatorRegistry>();
        var provider = services.BuildServiceProvider();

        var tracker = new ExcursionTracker(trackerRepo, time, NullLogger<ExcursionTracker>.Instance);
        var engine = new ManagedAlertEngine(
            provider.GetRequiredService<ConditionEvaluatorRegistry>(),
            tracker,
            NullLogger<ManagedAlertEngine>.Instance);
        return (engine, provider);
    }

    /// <summary>Builds a <see cref="RustBackedAlertEngine"/> over the same in-memory fakes.</summary>
    public static RustBackedAlertEngine BuildRustEngine(
        ManualTimeProvider time,
        IConditionTimerStore timerStore,
        InMemoryTrackerRepository trackerRepo)
    {
        var tracker = new ExcursionTracker(trackerRepo, time, NullLogger<ExcursionTracker>.Instance);
        return new RustBackedAlertEngine(
            timerStore,
            trackerRepo,
            tracker,
            time,
            NullLogger<RustBackedAlertEngine>.Instance);
    }

    // -----------------------------------------------------------------------
    // Scenario conversions (mirroring the generator's private helpers)
    // -----------------------------------------------------------------------

    public static AlertRule ToAlertRule(ScenarioRule rule)
    {
        var conditionType = AlertConditionTypeNames.FromWireString(rule.ConditionType)
            ?? throw new InvalidOperationException(
                $"Scenario rule '{rule.Name}' has unknown condition_type '{rule.ConditionType}'");

        return new AlertRule
        {
            Id = rule.Id,
            Name = rule.Name,
            ConditionType = conditionType,
            ConditionParams = rule.ConditionParams.GetRawText(),
            ConfirmationReadings = rule.ConfirmationReadings,
            HysteresisMinutes = rule.HysteresisMinutes,
            AutoResolveEnabled = rule.AutoResolveEnabled,
            AutoResolveParams = rule.AutoResolveParams?.GetRawText(),
        };
    }

    public static AlertRuleSnapshot ToSnapshot(ScenarioRule rule)
    {
        var conditionType = AlertConditionTypeNames.FromWireString(rule.ConditionType)
            ?? throw new InvalidOperationException(
                $"Scenario rule '{rule.Name}' has unknown condition_type '{rule.ConditionType}'");

        return new AlertRuleSnapshot(
            rule.Id,
            TenantId,
            rule.Name,
            conditionType,
            rule.ConditionParams.GetRawText(),
            AlertRuleSeverity.Warning,
            "{}",
            0,
            rule.AutoResolveEnabled,
            rule.AutoResolveParams?.GetRawText());
    }

    /// <summary>Mirrors ScenarioRunner.ToSensorContext (private in the generator).</summary>
    public static SensorContext ToSensorContext(ScenarioContext ctx)
    {
        return new SensorContext
        {
            LatestValue = ctx.LatestValue,
            LatestTimestamp = Utc(ctx.LatestTimestamp),
            TrendRate = ctx.TrendRate,
            LastReadingAt = Utc(ctx.LastReadingAt),
            TrendBucket = ParseWireEnum<TrendBucket>(ctx.TrendBucket),
            IobUnits = ctx.IobUnits,
            CobGrams = ctx.CobGrams,
            ReservoirUnits = ctx.ReservoirUnits,
            LastSiteChangeAt = Utc(ctx.LastSiteChangeAt),
            LastSensorStartAt = Utc(ctx.LastSensorStartAt),
            Predictions = ctx.Predictions?.Select(p => new PredictedGlucosePoint(p.OffsetMinutes, p.Mgdl)).ToList()
                ?? (IReadOnlyList<PredictedGlucosePoint>)Array.Empty<PredictedGlucosePoint>(),
            ActiveAlerts = ctx.ActiveAlerts?.ToDictionary(
                    a => a.AlertId,
                    a => new ActiveAlertSnapshot(a.State, Utc(a.TriggeredAt)!.Value, Utc(a.AcknowledgedAt)))
                ?? new Dictionary<Guid, ActiveAlertSnapshot>(),
            LastApsCycleAt = Utc(ctx.LastApsCycleAt),
            LastApsEnactedAt = Utc(ctx.LastApsEnactedAt),
            PumpBatteryPercent = ctx.PumpBatteryPercent,
            ActiveTempBasal = ctx.ActiveTempBasal is { } tb
                ? new TempBasalSnapshot(tb.Rate, tb.ScheduledRate, Utc(tb.StartedAt)!.Value)
                : null,
            UploaderBatteryPercent = ctx.UploaderBatteryPercent,
            ActiveOverride = ctx.ActiveOverride is { } ov
                ? new OverrideSnapshot(Utc(ov.StartedAt)!.Value, null, null, null)
                : null,
            ActivePumpSuspension = ctx.ActivePumpSuspension is { } ps
                ? new PumpSuspensionSnapshot(Utc(ps.StartedAt)!.Value)
                : null,
            SensitivityRatio = ctx.SensitivityRatio,
            ActiveDoNotDisturb = ctx.ActiveDoNotDisturb is { } dnd
                ? new DoNotDisturbSnapshot(Utc(dnd.StartedAt)!.Value, dnd.Source)
                : null,
            HasEverApsCycled = ctx.HasEverApsCycled,
            HasEverPumpSnapshot = ctx.HasEverPumpSnapshot,
            HasEverUploaderSnapshot = ctx.HasEverUploaderSnapshot,
            HasEverApsSensitivity = ctx.HasEverApsSensitivity,
            GlucoseBucket = ParseWireEnum<GlucoseBucket>(ctx.GlucoseBucket),
            LastCarbAt = Utc(ctx.LastCarbAt),
            LastBolusAt = Utc(ctx.LastBolusAt),
            TenantTimeZoneId = ctx.TenantTimeZoneId,
            ActivePumpState = ctx.ActivePumpState is { } pumpState
                ? new PumpStateSnapshot(
                    ParseWireEnum<PumpModeState>(pumpState.Mode)!.Value,
                    Utc(pumpState.StartedAt)!.Value)
                : null,
            ActiveStateSpans = ctx.ActiveStateSpans?.ToDictionary(
                    s => (ParseWireEnum<StateSpanCategory>(s.Category)!.Value, s.State),
                    s => new StateSpanSnapshot(
                        ParseWireEnum<StateSpanCategory>(s.Category)!.Value,
                        s.State,
                        Utc(s.StartedAt)!.Value))
                ?? new Dictionary<(StateSpanCategory, string?), StateSpanSnapshot>(),
        };
    }

    /// <summary>
    /// Drives the rule's smart-snooze conditions through the seam's auxiliary scope
    /// (<c>EvaluateNodeAsync</c> under the <c>snooze</c> path root) — the call the sweep
    /// makes, and the only corpus step that exercises a root-path override on either
    /// adapter. Returns null when the rule configures no conditions.
    /// </summary>
    public static async Task<bool?> EvaluateSnoozeAsync(
        IAlertEvaluationEngine engine,
        ScenarioRule rule,
        SensorContext context,
        CancellationToken ct)
    {
        if (rule.SnoozeConditions is not { Count: > 0 } rawConditions)
            return null;

        var conditions = rawConditions
            .Select(c => JsonSerializer.Deserialize<ConditionNode>(c.GetRawText(), EvaluatorJson.Options)
                ?? throw new InvalidOperationException(
                    $"Scenario rule '{rule.Name}' has a null snooze condition"))
            .ToList();

        return await engine.EvaluateNodeAsync(
            rule.Id,
            SnoozeConditionTree.Wrap(conditions),
            context,
            AlertConditionTypeNames.SnoozePathRoot,
            ct);
    }

    /// <summary>
    /// Assembles an <see cref="ExpectedRuleResult"/> from a seam evaluation plus the
    /// observable state in the fakes — the same projection the corpus generator records.
    /// </summary>
    public static async Task<ExpectedRuleResult> ToExpectedResultAsync(
        ScenarioRule rule,
        AlertEngineEvaluation evaluation,
        InMemoryTrackerRepository trackerRepo,
        RecordingTimerStore timerStore,
        bool? snoozeExtend,
        CancellationToken ct)
    {
        if (evaluation.Skipped)
        {
            return new ExpectedRuleResult { RuleId = rule.Id, Skipped = true };
        }

        var state = await trackerRepo.GetTrackerStateAsync(rule.Id, ct);

        return new ExpectedRuleResult
        {
            RuleId = rule.Id,
            Root = evaluation.ConditionMet,
            Leaves = (evaluation.LeafValues ?? new Dictionary<int, bool>())
                .OrderBy(kv => kv.Key)
                .Select(kv => new ExpectedLeaf(kv.Key, kv.Value))
                .ToList(),
            Transition = RustEnvelopeMapper.TransitionToWire(evaluation.Transition.Type),
            CloseReason = RustEnvelopeMapper.CloseReasonToWire(evaluation.Transition.CloseReason),
            Tracker = state is null
                ? null
                : new ExpectedTrackerState
                {
                    State = state.State,
                    ConfirmationCount = state.ConfirmationCount,
                    Excursion = trackerRepo.OrdinalOf(state.ActiveExcursionId),
                },
            AutoResolved = evaluation.AutoResolved ? true : null,
            SnoozeExtend = snoozeExtend,
            TimerOps = timerStore.DrainOps() is { Count: > 0 } ops ? ops : null,
        };
    }

    // -----------------------------------------------------------------------
    // JSON deep diff
    // -----------------------------------------------------------------------

    /// <summary>
    /// Recursively compares <paramref name="actual"/> against <paramref name="expected"/>,
    /// appending one human-readable line per difference (path, expected, actual).
    /// </summary>
    public static void Diff(JsonNode? actual, JsonNode? expected, string path, List<string> failures)
    {
        if (JsonNode.DeepEquals(actual, expected)) return;

        if (actual is JsonObject actualObj && expected is JsonObject expectedObj)
        {
            foreach (var key in actualObj.Select(kv => kv.Key).Union(expectedObj.Select(kv => kv.Key)))
            {
                var a = actualObj.TryGetPropertyValue(key, out var av) ? av : null;
                var e = expectedObj.TryGetPropertyValue(key, out var ev) ? ev : null;
                Diff(a, e, $"{path}.{key}", failures);
            }
            return;
        }

        if (actual is JsonArray actualArr && expected is JsonArray expectedArr)
        {
            if (actualArr.Count != expectedArr.Count)
            {
                failures.Add($"{path}: array length expected {expectedArr.Count}, actual {actualArr.Count}");
                return;
            }
            for (var i = 0; i < actualArr.Count; i++)
            {
                Diff(actualArr[i], expectedArr[i], $"{path}[{i}]", failures);
            }
            return;
        }

        failures.Add($"{path}: expected {expected?.ToJsonString() ?? "null"}, actual {actual?.ToJsonString() ?? "null"}");
    }

    private static DateTime? Utc(DateTime? value) =>
        value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private static TEnum? ParseWireEnum<TEnum>(string? wire) where TEnum : struct, Enum =>
        wire is null ? null : JsonSerializer.Deserialize<TEnum>($"\"{wire}\"", EnumWireOptions);
}

/// <summary>Minimal capturing logger for asserting structured log events.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
