using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Alerts.Corpus;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Tests.Services.Alerts.Engines;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// The watermark contract: a canonical reading is evaluated once, and the pass that was skipped
/// is the repeat, never the reading.
/// </summary>
[Trait("Category", "Unit")]
public class CanonicalAlertEvaluatorTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime Reading = new(2026, 9, 9, 7, 15, 0, DateTimeKind.Utc);

    private readonly Mock<ICanonicalGlucoseService> _canonical = new();
    private readonly Mock<IAlertOrchestrator> _orchestrator = new();
    private readonly Mock<IAlertRepository> _rules = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Reading, TimeSpan.Zero));
    private readonly Mock<IClockAnomalyNotice> _notice = new();
    private readonly AlertEvaluationWatermark _watermark;
    private readonly AlertRuleDecisions _decisions;

    private static readonly MovedFacts GlucoseMoved = Moved(ContextFactWrites.Glucose);

    private static MovedFacts Moved(params string[] facts) => new(facts.ToHashSet());

    public CanonicalAlertEvaluatorTests()
    {
        _watermark = new AlertEvaluationWatermark(_clock);
        _decisions = new AlertRuleDecisions(_clock);
        RulesAre(Rule(AlertConditionType.Threshold, """{"direction":"below","value":70}"""));
    }

    private static AlertRuleSnapshot Rule(AlertConditionType type, string json) =>
        new(Guid.NewGuid(), Tenant, type.ToString(), type, json, AlertRuleSeverity.Warning, "{}", 0,
            AutoResolveEnabled: false, AutoResolveParams: null);

    private void RulesAre(params AlertRuleSnapshot[] rules) =>
        _rules.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(rules);

    private static SensorGlucose Latest(DateTime timestamp, double mgdl = 120, double? trendRate = 0.5) =>
        new() { Timestamp = timestamp, CreatedAt = timestamp, Mgdl = mgdl, TrendRate = trendRate };

    private CanonicalAlertEvaluator Evaluator(Guid tenantId, AlertRuleDecisions? decisions = null) =>
        new(_canonical.Object,
            _orchestrator.Object,
            _rules.Object,
            Mock.Of<ITenantAccessor>(a => a.TenantId == tenantId),
            _watermark,
            decisions ?? _decisions,
            _notice.Object,
            NullLogger<CanonicalAlertEvaluator>.Instance);

    private void LatestIs(params SensorGlucose?[] readings)
    {
        var queue = new Queue<SensorGlucose?>(readings);
        _canonical
            .Setup(c => c.GetLatestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => queue.Count > 1 ? queue.Dequeue() : queue.Peek());
    }

    private void VerifyPasses(int times) =>
        _orchestrator.Verify(
            o => o.EvaluateRulesAsync(It.IsAny<IReadOnlyList<AlertRuleSnapshot>>(), It.IsAny<IReadOnlySet<Guid>>(), It.IsAny<SensorContext>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(times));

    [Fact]
    public async Task EvaluateAsync_EvaluatesTheFirstTimeAReadingIsSeen()
    {
        LatestIs(Latest(Reading));

        await Evaluator(Tenant).EvaluateAsync(GlucoseMoved);

        VerifyPasses(1);
    }

    [Fact]
    public async Task EvaluateAsync_SkipsARepeatOfTheSameReading()
    {
        // A sync publishes in chunks and an uploader re-posts stored readings, so the same
        // canonical reading arrives here many times over.
        LatestIs(Latest(Reading));
        var evaluator = Evaluator(Tenant);

        await evaluator.EvaluateAsync(GlucoseMoved);
        await evaluator.EvaluateAsync(GlucoseMoved);
        await evaluator.EvaluateAsync(GlucoseMoved);

        VerifyPasses(1);
    }

    [Fact]
    public async Task EvaluateAsync_SkipsARepeatAcrossScopes()
    {
        // The evaluator is scoped and a connector's chunks span scopes, so the watermark has to
        // outlive the instance that recorded it.
        LatestIs(Latest(Reading));

        await Evaluator(Tenant).EvaluateAsync(GlucoseMoved);
        await Evaluator(Tenant).EvaluateAsync(GlucoseMoved);

        VerifyPasses(1);
    }

    [Fact]
    public async Task EvaluateAsync_EvaluatesAgainWhenTheStreamAdvances()
    {
        LatestIs(Latest(Reading), Latest(Reading.AddMinutes(5)));
        var evaluator = Evaluator(Tenant);

        await evaluator.EvaluateAsync(GlucoseMoved);
        await evaluator.EvaluateAsync(GlucoseMoved);

        VerifyPasses(2);
    }

    [Fact]
    public async Task EvaluateAsync_EvaluatesAgainWhenTheValueIsCorrectedAtTheSameTimestamp()
    {
        LatestIs(Latest(Reading, mgdl: 120), Latest(Reading, mgdl: 250));
        var evaluator = Evaluator(Tenant);

        await evaluator.EvaluateAsync(GlucoseMoved);
        await evaluator.EvaluateAsync(GlucoseMoved);

        VerifyPasses(2);
    }

    [Fact]
    public async Task EvaluateAsync_EvaluatesAgainWhenOnlyTheTrendRateChanges()
    {
        LatestIs(Latest(Reading, trendRate: 0.5), Latest(Reading, trendRate: -3.5));
        var evaluator = Evaluator(Tenant);

        await evaluator.EvaluateAsync(GlucoseMoved);
        await evaluator.EvaluateAsync(GlucoseMoved);

        VerifyPasses(2);
    }

    [Fact]
    public async Task EvaluateAsync_EvaluatesAgainWhenAnEarlierReadingBecomesLatest()
    {
        // A delete of the newest row moves the canonical latest backwards; that is a different
        // decision, not a repeat of one already made.
        LatestIs(Latest(Reading), Latest(Reading.AddMinutes(-5)));
        var evaluator = Evaluator(Tenant);

        await evaluator.EvaluateAsync(GlucoseMoved);
        await evaluator.EvaluateAsync(GlucoseMoved);

        VerifyPasses(2);
    }

    [Fact]
    public async Task EvaluateAsync_RetriesAReadingWhosePassFailed()
    {
        LatestIs(Latest(Reading));
        _orchestrator
            .SetupSequence(o => o.EvaluateRulesAsync(It.IsAny<IReadOnlyList<AlertRuleSnapshot>>(), It.IsAny<IReadOnlySet<Guid>>(), It.IsAny<SensorContext>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("engine unavailable"))
            .Returns(Task.CompletedTask);
        var evaluator = Evaluator(Tenant);

        await evaluator.EvaluateAsync(GlucoseMoved);
        await evaluator.EvaluateAsync(GlucoseMoved);

        VerifyPasses(2);
    }

    [Fact]
    public async Task EvaluateAsync_KeepsAWatermarkPerTenant()
    {
        var other = Guid.Parse("22222222-2222-2222-2222-222222222222");
        LatestIs(Latest(Reading));

        await Evaluator(Tenant).EvaluateAsync(GlucoseMoved);
        await Evaluator(other).EvaluateAsync(GlucoseMoved);

        VerifyPasses(2);
    }

    [Fact]
    public void MaxSkipWindow_IsOneMinute()
    {
        // The bound on how late a clock-driven escalation can be. The window tests below are
        // written against the symbol, so without this the value itself is free to drift.
        AlertEvaluationWatermark.MaxSkipWindow.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task EvaluateAsync_EvaluatesTheSameReadingAgainOnceTheSkipWindowExpires()
    {
        // A rule can turn true on elapsed time alone while the reading stands still: alert_state
        // with forMinutes is the documented way to express delayed escalation, and no sweep path
        // opens an excursion for it. The reading is a reason to skip a repeat, not to stop.
        LatestIs(Latest(Reading));
        var evaluator = Evaluator(Tenant);

        await evaluator.EvaluateAsync(GlucoseMoved);
        _clock.Advance(AlertEvaluationWatermark.MaxSkipWindow - TimeSpan.FromMilliseconds(1));
        await evaluator.EvaluateAsync(GlucoseMoved);
        VerifyPasses(1);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await evaluator.EvaluateAsync(GlucoseMoved);

        VerifyPasses(2);
    }

    [Fact]
    public async Task EvaluateAsync_RestartsTheSkipWindowFromTheLatestPass()
    {
        // The window measures staleness of the last pass, not of the first sighting, so a stream
        // of repeats cannot walk the tenant past it without ever evaluating.
        LatestIs(Latest(Reading));
        var evaluator = Evaluator(Tenant);

        await evaluator.EvaluateAsync(GlucoseMoved);
        _clock.Advance(AlertEvaluationWatermark.MaxSkipWindow);
        await evaluator.EvaluateAsync(GlucoseMoved);
        VerifyPasses(2);

        _clock.Advance(AlertEvaluationWatermark.MaxSkipWindow - TimeSpan.FromSeconds(1));
        await evaluator.EvaluateAsync(GlucoseMoved);

        VerifyPasses(2);
    }

    [Fact]
    public async Task EvaluateAsync_EvaluatesNothingAndRemembersNothingWithNoTenantInScope()
    {
        LatestIs(Latest(Reading));

        await Evaluator(Guid.Empty).EvaluateAsync(GlucoseMoved);

        VerifyPasses(0);
        _watermark.AlreadyEvaluated(Guid.Empty, Latest(Reading)).Should().BeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_DoesNotEvaluateWithoutAPositiveReading()
    {
        LatestIs(Latest(Reading, mgdl: 0));

        await Evaluator(Tenant).EvaluateAsync(GlucoseMoved);

        VerifyPasses(0);
    }

    [Fact]
    public async Task EvaluateAsync_PassesTheCanonicalReadingIntoTheContext()
    {
        LatestIs(Latest(Reading, mgdl: 187, trendRate: -1.5));
        SensorContext? seen = null;
        _orchestrator
            .Setup(o => o.EvaluateRulesAsync(It.IsAny<IReadOnlyList<AlertRuleSnapshot>>(), It.IsAny<IReadOnlySet<Guid>>(), It.IsAny<SensorContext>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AlertRuleSnapshot>, IReadOnlySet<Guid>, SensorContext, long?, CancellationToken>((_, _, c, _, _) => seen = c)
            .Returns(Task.CompletedTask);

        await Evaluator(Tenant).EvaluateAsync(GlucoseMoved);

        seen.Should().NotBeNull();
        seen!.LatestValue.Should().Be(187m);
        seen.LatestTimestamp.Should().Be(Reading);
        seen.TrendRate.Should().Be(-1.5m);
        seen.LastReadingAt.Should().Be(Reading);
    }

    [Fact]
    public async Task EvaluateAsync_ContainsAnUnexpectedFailureOfThePass()
    {
        // The pass runs off the write's request; a fault in it must not stop the next one.
        LatestIs(Latest(Reading));
        _orchestrator
            .Setup(o => o.EvaluateRulesAsync(It.IsAny<IReadOnlyList<AlertRuleSnapshot>>(), It.IsAny<IReadOnlySet<Guid>>(), It.IsAny<SensorContext>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NullReferenceException());

        var act = () => Evaluator(Tenant).EvaluateAsync(GlucoseMoved);

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// A Dexcom or Glooko reading stores only its arrow. The pass judges it by the rate derived
    /// from the previous reading of its stream, so a <c>rate_of_change</c> and a <c>trend</c> rule
    /// fire over a fall of readings that carry no stored rate.
    /// </summary>
    [NativeFact]
    public async Task EvaluateAsync_RateAndTrendRulesFireOverAFallWithNoStoredRate()
    {
        var latest = new SensorGlucose
        {
            Id = Guid.NewGuid(), Timestamp = Reading, CreatedAt = Reading, Mgdl = 100, DataSource = "dexcom", Device = "g7",
        };
        LatestIs(latest);
        _canonical
            .Setup(c => c.GetStreamPredecessorAsync(latest, Reading - ReadingTrend.MaxGap, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensorGlucose
            {
                Id = Guid.NewGuid(), Timestamp = Reading.AddMinutes(-5), Mgdl = 118, DataSource = "dexcom", Device = "g7",
            });
        SensorContext? seen = null;
        _orchestrator
            .Setup(o => o.EvaluateRulesAsync(It.IsAny<IReadOnlyList<AlertRuleSnapshot>>(), It.IsAny<IReadOnlySet<Guid>>(), It.IsAny<SensorContext>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AlertRuleSnapshot>, IReadOnlySet<Guid>, SensorContext, long?, CancellationToken>((_, _, c, _, _) => seen = c)
            .Returns(Task.CompletedTask);

        await Evaluator(Tenant).EvaluateAsync(GlucoseMoved);

        seen!.TrendRate.Should().Be(-3.6m);
        seen.TrendBucket.Should().Be(TrendBucket.FallingFast);
        var time = new ManualTimeProvider();
        time.SetUtcNow(Reading);
        var engine = EngineTestHarness.BuildRustEngine(time, new InMemoryTrackerRepository([]));
        foreach (var (type, json) in new[]
                 {
                     (AlertConditionType.RateOfChange, """{"direction":"falling","rate":3}"""),
                     (AlertConditionType.Trend, """{"bucket":"falling_fast"}"""),
                 })
        {
            var rule = new AlertRuleSnapshot(Guid.NewGuid(), Tenant, "fall", type, json,
                AlertRuleSeverity.Warning, "{}", 0, AutoResolveEnabled: false, AutoResolveParams: null);
            var evaluation = await engine.EvaluateRuleAsync(rule, seen, new AlertEngineOptions(), CancellationToken.None);
            evaluation.ConditionMet.Should().BeTrue(type.ToString());
        }
    }

    /// <summary>
    /// A reading dated an hour ahead is stored as the newest, then a real reading of 60 arrives:
    /// the context is built from the real one, so a low rule opens on it and signal loss counts
    /// from it, and the skipped reading goes to the clock notice
    /// (docs/alerts/engine-semantics.md §4).
    /// </summary>
    [NativeFact]
    public async Task EvaluateAsync_AReadingDatedBeyondTheToleranceIsSkippedForTheRealOne()
    {
        var future = Latest(Reading.AddHours(1), mgdl: 300);
        var real = Latest(Reading, mgdl: 60, trendRate: -1);
        LatestIs(future);
        _canonical
            .Setup(c => c.GetLatestAsync(Reading + RustAlertEngine.ClockTolerance(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(real);
        SensorContext? seen = null;
        _orchestrator
            .Setup(o => o.EvaluateRulesAsync(It.IsAny<IReadOnlyList<AlertRuleSnapshot>>(), It.IsAny<IReadOnlySet<Guid>>(), It.IsAny<SensorContext>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AlertRuleSnapshot>, IReadOnlySet<Guid>, SensorContext, long?, CancellationToken>((_, _, c, _, _) => seen = c)
            .Returns(Task.CompletedTask);

        await Evaluator(Tenant).EvaluateAsync(GlucoseMoved);

        seen!.LatestValue.Should().Be(60m);
        seen.LastReadingAt.Should().Be(Reading);
        _notice.Verify(n => n.ReportReadingAsync(
            It.Is<AlertReadingSelection>(s => s.Selected == real && s.Newest == future), Reading,
            It.IsAny<CancellationToken>()), Times.Once);

        var time = new ManualTimeProvider();
        time.SetUtcNow(Reading);
        var engine = EngineTestHarness.BuildRustEngine(time, new InMemoryTrackerRepository([]));
        var low = new AlertRuleSnapshot(Guid.NewGuid(), Tenant, "low", AlertConditionType.Threshold,
            """{"direction":"below","value":70}""", AlertRuleSeverity.Critical, "{}", 0, false, null);
        (await engine.EvaluateRuleAsync(low, seen, new AlertEngineOptions(), CancellationToken.None))
            .ConditionMet.Should().BeTrue("the low rule reads the real reading of 60");

        time.SetUtcNow(Reading.AddMinutes(20));
        var signalLoss = new AlertRuleSnapshot(Guid.NewGuid(), Tenant, "signal", AlertConditionType.SignalLoss,
            """{"timeout_minutes":20}""", AlertRuleSeverity.Critical, "{}", 0, false, null);
        var lost = await engine.EvaluateRuleAsync(signalLoss, seen, new AlertEngineOptions(), CancellationToken.None);
        lost.ConditionMet.Should().BeTrue("signal loss counts from the last real reading");
        lost.ClockAnomalies.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_AReadingWithinTheToleranceIsSelectedOnceItsTimeComesNear()
    {
        var future = Latest(Reading.AddHours(1));
        LatestIs(future);
        _clock.SetUtcNow(new DateTimeOffset(future.Timestamp - RustAlertEngine.ClockTolerance(), TimeSpan.Zero));
        SensorContext? seen = null;
        _orchestrator
            .Setup(o => o.EvaluateRulesAsync(It.IsAny<IReadOnlyList<AlertRuleSnapshot>>(), It.IsAny<IReadOnlySet<Guid>>(), It.IsAny<SensorContext>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AlertRuleSnapshot>, IReadOnlySet<Guid>, SensorContext, long?, CancellationToken>((_, _, c, _, _) => seen = c)
            .Returns(Task.CompletedTask);

        await Evaluator(Tenant).EvaluateAsync(GlucoseMoved);

        seen!.LastReadingAt.Should().Be(future.Timestamp);
    }

    [Fact]
    public async Task EvaluateAsync_AFailingClockNoticeDoesNotSkipTheReading()
    {
        LatestIs(Latest(Reading));
        _notice
            .Setup(n => n.ReportReadingAsync(It.IsAny<AlertReadingSelection>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("lock timeout"));

        await Evaluator(Tenant).EvaluateAsync(GlucoseMoved);

        VerifyPasses(1);
        _watermark.AlreadyEvaluated(Tenant, Latest(Reading)).Should().BeTrue("the pass completed");
    }

    [Fact]
    public async Task EvaluateAsync_ReadsOnceWhenNoReadingIsAhead()
    {
        LatestIs(Latest(Reading));

        await Evaluator(Tenant).EvaluateAsync(GlucoseMoved);

        _canonical.Verify(c => c.GetLatestAsync(It.IsAny<CancellationToken>()), Times.Once);
        _canonical.Verify(c => c.GetLatestAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyPasses(1);
    }

    [Fact]
    public async Task EvaluateAsync_AStoredRateReadsNoEarlierReading()
    {
        LatestIs(Latest(Reading, trendRate: -1.5));

        await Evaluator(Tenant).EvaluateAsync(GlucoseMoved);

        _canonical.Verify(
            c => c.GetStreamPredecessorAsync(It.IsAny<SensorGlucose>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static readonly AlertRuleSnapshot LowRule =
        Rule(AlertConditionType.Threshold, """{"direction":"below","value":70}""");

    private static readonly AlertRuleSnapshot ReservoirRule =
        Rule(AlertConditionType.Reservoir, """{"operator":"<","value":20}""");

    private static readonly AlertRuleSnapshot LoopRule =
        Rule(AlertConditionType.LoopStale, """{"operator":">","minutes":15}""");

    private IReadOnlyList<AlertRuleSnapshot> EvaluatedRules()
    {
        var call = _orchestrator.Invocations.Should().ContainSingle(i => i.Method.Name == nameof(IAlertOrchestrator.EvaluateRulesAsync)).Subject;
        return (IReadOnlyList<AlertRuleSnapshot>)call.Arguments[0]!;
    }

    [NativeFact]
    public async Task EvaluateAsync_APumpWriteEvaluatesOnlyTheRulesThatReadThePump()
    {
        LatestIs(Latest(Reading));
        RulesAre(LowRule, ReservoirRule, LoopRule);

        await Evaluator(Tenant).EvaluateAsync(Moved(ContextFactWrites.Pump, ContextFactWrites.Insulin));

        EvaluatedRules().Should().Equal(ReservoirRule);
        var enabled = (IReadOnlySet<Guid>)_orchestrator.Invocations.Single().Arguments[1]!;
        enabled.Should().BeEquivalentTo([LowRule.Id, ReservoirRule.Id, LoopRule.Id], "alert_state resolves against every enabled rule");
    }

    [NativeFact]
    public async Task EvaluateAsync_AGlucoseWriteEvaluatesEveryEnabledRule()
    {
        LatestIs(Latest(Reading));
        RulesAre(LowRule, ReservoirRule, LoopRule);

        await Evaluator(Tenant).EvaluateAsync(GlucoseMoved);

        EvaluatedRules().Should().Equal(LowRule, ReservoirRule, LoopRule);
    }

    [NativeFact]
    public async Task EvaluateAsync_ARepeatedReadingStillEvaluatesTheRulesItsOtherFactsMoved()
    {
        LatestIs(Latest(Reading));
        RulesAre(LowRule, ReservoirRule, LoopRule);
        var evaluator = Evaluator(Tenant);
        await evaluator.EvaluateAsync(GlucoseMoved);
        _orchestrator.Invocations.Clear();

        await evaluator.EvaluateAsync(Moved(ContextFactWrites.Glucose, ContextFactWrites.Aps));

        EvaluatedRules().Should().Equal(LoopRule);
    }

    [NativeFact]
    public async Task EvaluateAsync_AWriteNoRuleReadsEvaluatesNothing()
    {
        LatestIs(Latest(Reading));
        RulesAre(LowRule, ReservoirRule);

        await Evaluator(Tenant).EvaluateAsync(Moved(ContextFactWrites.Uploader));

        VerifyPasses(0);
    }

    [NativeFact]
    public async Task EvaluateAsync_WithoutAUsableReadingAPumpWriteEvaluatesNothing()
    {
        LatestIs(Latest(Reading, mgdl: 0));
        RulesAre(ReservoirRule);

        await Evaluator(Tenant).EvaluateAsync(Moved(ContextFactWrites.Pump));

        VerifyPasses(0);
    }

    [NativeFact]
    public async Task EvaluateAsync_APassOverlappingOneStillDeliveringOnTheSameReadingStopsAfterItsRead()
    {
        LatestIs(Latest(Reading));
        var delivering = new TaskCompletionSource();
        _orchestrator
            .Setup(o => o.EvaluateRulesAsync(It.IsAny<IReadOnlyList<AlertRuleSnapshot>>(), It.IsAny<IReadOnlySet<Guid>>(), It.IsAny<SensorContext>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns(delivering.Task);

        var first = Evaluator(Tenant).EvaluateAsync(GlucoseMoved);
        await Evaluator(Tenant).EvaluateAsync(GlucoseMoved);

        VerifyPasses(1);
        first.IsCompleted.Should().BeFalse();
        delivering.SetResult();
        await first;
    }

    [NativeFact]
    public async Task EvaluateAsync_AGlucoseMoveWithoutAReadingEvaluatesTheGlucoseRulesPastTheWatermark()
    {
        var bucket = Rule(AlertConditionType.GlucoseBucket, """{"buckets":["low"]}""");
        LatestIs(Latest(Reading));
        RulesAre(LowRule, bucket, ReservoirRule);
        var evaluator = Evaluator(Tenant);
        await evaluator.EvaluateAsync(GlucoseMoved);
        _orchestrator.Invocations.Clear();

        await evaluator.EvaluateAsync(new MovedFacts(new HashSet<string> { ContextFactWrites.Glucose }, GlucoseWithoutReading: true));

        EvaluatedRules().Should().Equal(LowRule, bucket);
    }

    [Fact]
    public void ATargetRangeWriteMovesGlucoseWithoutAReadingAndASensorGlucoseWriteDoesNot()
    {
        ContextFactWrites.MovedIn(RecordsChanged.Written([typeof(TargetRangeSchedule)])).GlucoseWithoutReading.Should().BeTrue();
        ContextFactWrites.MovedIn(RecordsChanged.Written([typeof(SensorGlucose)])).GlucoseWithoutReading.Should().BeFalse();
    }

    [NativeFact]
    public async Task EvaluateAsync_HandsTheOrchestratorTheReadItsContextCameFrom()
    {
        LatestIs(Latest(Reading));
        long? readAt = null;
        _orchestrator
            .Setup(o => o.EvaluateRulesAsync(It.IsAny<IReadOnlyList<AlertRuleSnapshot>>(), It.IsAny<IReadOnlySet<Guid>>(), It.IsAny<SensorContext>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AlertRuleSnapshot>, IReadOnlySet<Guid>, SensorContext, long?, CancellationToken>((_, _, _, r, _) => readAt = r)
            .Returns(Task.CompletedTask);
        var before = _decisions.BeginRead().Sequence;

        await Evaluator(Tenant).EvaluateAsync(GlucoseMoved);

        readAt.Should().BeGreaterThan(before).And.BeLessThan(_decisions.BeginRead().Sequence);
    }

    /// <summary>A wall clock a test can step back, as an NTP correction does.</summary>
    private sealed class SteppingClock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private async Task<SensorContext> PumpPassAsync(AlertRuleDecisions? decisions = null)
    {
        SensorContext? seen = null;
        _orchestrator
            .Setup(o => o.EvaluateRulesAsync(It.IsAny<IReadOnlyList<AlertRuleSnapshot>>(), It.IsAny<IReadOnlySet<Guid>>(), It.IsAny<SensorContext>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AlertRuleSnapshot>, IReadOnlySet<Guid>, SensorContext, long?, CancellationToken>((_, _, c, _, _) => seen = c)
            .Returns(Task.CompletedTask);
        await Evaluator(Tenant, decisions).EvaluateAsync(Moved(ContextFactWrites.Pump));
        return seen!;
    }

    private void ReadingsAre(SensorGlucose older, SensorGlucose newer)
    {
        _canonical.Setup(c => c.GetLatestAsync(It.IsAny<CancellationToken>())).ReturnsAsync(newer);
        _canonical
            .Setup(c => c.GetLatestAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime notAfter, CancellationToken _) => newer.Timestamp <= notAfter ? newer : older);
    }

    [NativeFact]
    public async Task EvaluateAsync_ALaterPassSelectsTheReadingTheClockHasReached()
    {
        RulesAre(ReservoirRule);
        var older = Latest(Reading, mgdl: 80);
        var newer = Latest(Reading + RustAlertEngine.ClockTolerance() + TimeSpan.FromSeconds(1), mgdl: 52);
        ReadingsAre(older, newer);

        (await PumpPassAsync()).LatestValue.Should().Be(80m);
        _clock.Advance(TimeSpan.FromSeconds(2));
        (await PumpPassAsync()).LatestValue.Should().Be(52m);
    }

    [NativeFact]
    public async Task EvaluateAsync_ALaterPassStillSelectsTheReadingWhenTheClockStepsBack()
    {
        RulesAre(ReservoirRule);
        var older = Latest(Reading, mgdl: 80);
        var newer = Latest(Reading + RustAlertEngine.ClockTolerance(), mgdl: 52);
        ReadingsAre(older, newer);

        var wallClock = new SteppingClock(Reading);
        var decisions = new AlertRuleDecisions(wallClock);

        (await PumpPassAsync(decisions)).LatestValue.Should().Be(52m);
        wallClock.Now = Reading.AddSeconds(-10);
        (await PumpPassAsync(decisions)).LatestValue.Should().Be(52m, "a later read never bounds its selection earlier");
    }
}
