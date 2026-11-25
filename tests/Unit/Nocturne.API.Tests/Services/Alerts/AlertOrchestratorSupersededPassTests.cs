using System.Collections.Concurrent;
using FluentAssertions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// A pass skips only the rules a newer context has already decided (<see cref="AlertRuleDecisions"/>),
/// rule by rule: a delivery that holds an older pass cannot let it drive a rule a newer reading
/// decided, and a rule the newer pass never evaluated is still evaluated by the older one.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AlertOrchestratorSupersededPassTests
{
    private static readonly Guid Tenant = Guid.Parse("36263626-0000-7000-8000-0000000000aa");
    private static readonly DateTime At = new(2026, 11, 22, 9, 0, 0, DateTimeKind.Utc);
    private static readonly AlertRuleSnapshot First = Rule("first");
    private static readonly AlertRuleSnapshot Second = Rule("second");
    private static readonly AlertRuleSnapshot Third = Rule("third");

    private readonly AlertRuleDecisions _decisions = new(TimeProvider.System);
    private readonly ConcurrentQueue<(Guid Rule, decimal? Value)> _evaluated = new();
    private readonly TaskCompletionSource _firstDeliveryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstDeliveryReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _deliveries;

    /// <summary>The reading value whose enrichment throws, standing for a transient store failure.</summary>
    private decimal? _enrichmentFailsFor;

    /// <summary>The rule and reading value whose engine evaluation throws.</summary>
    private (Guid Rule, decimal Value)? _engineFailsFor;

    private static AlertRuleSnapshot Rule(string name) =>
        new(Guid.NewGuid(), Tenant, name, AlertConditionType.Threshold, """{"direction":"below","value":70}""",
            AlertRuleSeverity.Critical, "{}", 0, AutoResolveEnabled: false, AutoResolveParams: null);

    private static SensorGlucose Reading(int minutes, double mgdl) =>
        new() { Timestamp = At.AddMinutes(minutes), CreatedAt = At.AddMinutes(minutes), Mgdl = mgdl };

    private AlertOrchestrator Orchestrator()
    {
        var engine = new Mock<IAlertEvaluationEngine>();
        engine
            .Setup(e => e.EvaluateRuleAsync(It.IsAny<AlertRuleSnapshot>(), It.IsAny<SensorContext>(),
                It.IsAny<AlertEngineOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AlertRuleSnapshot rule, SensorContext context, AlertEngineOptions _, CancellationToken _) =>
            {
                if (_engineFailsFor is { } failing && failing.Rule == rule.Id && failing.Value == context.LatestValue)
                    throw new TimeoutException("store unavailable inside the engine");
                _evaluated.Enqueue((rule.Id, context.LatestValue));
                return new AlertEngineEvaluation
                {
                    Transition = new ExcursionTransition(ExcursionTransitionType.ExcursionOpened, Guid.NewGuid()),
                };
            });

        var opened = new Mock<IExcursionOpenedHandler>();
        opened
            .Setup(h => h.HandleOpenedAsync(It.IsAny<AlertRuleSnapshot>(), It.IsAny<Guid>(), It.IsAny<SensorContext>(),
                It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<IReadOnlyList<AlertMetPoint>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (Interlocked.Increment(ref _deliveries) != 1) return;
                _firstDeliveryEntered.SetResult();
                await _firstDeliveryReleased.Task;
            });

        var enricher = new Mock<ISensorContextEnricher>();
        enricher
            .Setup(x => x.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SensorContext c, IEnumerable<AlertRuleSnapshot> _, CancellationToken _) =>
                c.LatestValue == _enrichmentFailsFor ? throw new TimeoutException("store unavailable") : c);

        return new AlertOrchestrator(
            engine.Object,
            new AlertRuleEvaluationGate(),
            _decisions,
            Mock.Of<ITenantAccessor>(a => a.TenantId == Tenant),
            enricher.Object,
            opened.Object,
            Mock.Of<IExcursionResolutionHandler>(),
            Mock.Of<IUnevaluableRuleNotice>(),
            Mock.Of<IClockAnomalyNotice>(),
            new ListLogger<AlertOrchestrator>());
    }

    private Task<AlertPassOutcome> PassAsync(AlertOrchestrator orchestrator, SensorGlucose reading, long readAt, params AlertRuleSnapshot[] rules) =>
        orchestrator.EvaluateRulesAsync(
            rules, rules.Select(r => r.Id).ToHashSet(), CanonicalAlertEvaluator.ContextFor(reading, default),
            readAt, CancellationToken.None);

    /// <summary>Starts pass A on <paramref name="older"/> and holds it in rule <see cref="First"/>'s delivery.</summary>
    private async Task<Task<AlertPassOutcome>> HoldPassAAsync(AlertOrchestrator orchestrator, SensorGlucose older)
    {
        var passA = PassAsync(orchestrator, older, _decisions.BeginRead().Sequence, First, Second, Third);
        await _firstDeliveryEntered.Task.WaitAsync(Eventually.DefaultPatience);
        return passA;
    }

    private async Task ReleaseAsync(Task passA)
    {
        _firstDeliveryReleased.SetResult();
        await passA.WaitAsync(Eventually.DefaultPatience);
    }

    [Fact]
    public async Task An_older_pass_skips_only_the_rules_a_newer_reading_decided_and_goes_on()
    {
        var orchestrator = Orchestrator();
        var passA = await HoldPassAAsync(orchestrator, Reading(0, 80));

        (await PassAsync(orchestrator, Reading(5, 52), _decisions.BeginRead().Sequence, First, Second).WaitAsync(Eventually.DefaultPatience))
            .Should().Be(AlertPassOutcome.Decided);
        await ReleaseAsync(passA);

        _evaluated.Should().Equal(
            (First.Id, 80m), (First.Id, 52m), (Second.Id, 52m), (Third.Id, 80m));
        (await passA).Should().Be(AlertPassOutcome.Superseded);
    }

    [Fact]
    public async Task A_newer_pass_that_failed_before_deciding_skips_nothing()
    {
        var orchestrator = Orchestrator();
        var passA = await HoldPassAAsync(orchestrator, Reading(0, 80));
        _enrichmentFailsFor = 52m;

        var passB = () => PassAsync(orchestrator, Reading(5, 52), _decisions.BeginRead().Sequence, First, Second, Third);
        await passB.Should().ThrowAsync<TimeoutException>();
        await ReleaseAsync(passA);

        _evaluated.Should().Equal((First.Id, 80m), (Second.Id, 80m), (Third.Id, 80m));
    }

    [Fact]
    public async Task A_rule_whose_evaluation_threw_in_the_newer_pass_is_still_evaluated_by_the_older_one()
    {
        var orchestrator = Orchestrator();
        var passA = await HoldPassAAsync(orchestrator, Reading(0, 80));
        _engineFailsFor = (Second.Id, 52m);

        (await PassAsync(orchestrator, Reading(5, 52), _decisions.BeginRead().Sequence, First, Second, Third).WaitAsync(Eventually.DefaultPatience))
            .Should().Be(AlertPassOutcome.Failed);
        await ReleaseAsync(passA);

        _evaluated.Should().Equal((First.Id, 80m), (First.Id, 52m), (Third.Id, 52m), (Second.Id, 80m));
        (await passA).Should().Be(AlertPassOutcome.Superseded, "it skipped the rule the newer pass decided, and none of its own threw");
    }

    [Fact]
    public async Task A_pass_with_a_superseded_rule_then_a_rule_that_threw_failed()
    {
        var orchestrator = Orchestrator();
        var passA = await HoldPassAAsync(orchestrator, Reading(0, 80));
        _engineFailsFor = (Third.Id, 80m);

        await PassAsync(orchestrator, Reading(5, 52), _decisions.BeginRead().Sequence, Second).WaitAsync(Eventually.DefaultPatience);
        await ReleaseAsync(passA);

        _evaluated.Should().Equal((First.Id, 80m), (Second.Id, 52m));
        (await passA).Should().Be(AlertPassOutcome.Failed, "a rule that threw is undecided, whatever another rule's pass skipped");
    }

    [Fact]
    public async Task A_pass_with_a_rule_that_threw_then_a_superseded_rule_failed()
    {
        var orchestrator = Orchestrator();
        var passA = await HoldPassAAsync(orchestrator, Reading(0, 80));
        _engineFailsFor = (Second.Id, 80m);

        await PassAsync(orchestrator, Reading(5, 52), _decisions.BeginRead().Sequence, Third).WaitAsync(Eventually.DefaultPatience);
        await ReleaseAsync(passA);

        _evaluated.Should().Equal((First.Id, 80m), (Third.Id, 52m));
        (await passA).Should().Be(AlertPassOutcome.Failed, "a later skip does not hide the rule that threw");
    }

    [Fact]
    public void A_decision_from_an_earlier_read_of_the_same_reading_does_not_move_the_record_back()
    {
        _decisions.Decided(First.Id, Stamp(Reading(5, 52), readAt: 9));
        _decisions.Decided(First.Id, Stamp(Reading(5, 52), readAt: 5));

        _decisions.DecidedByNewer(First.Id, Stamp(Reading(0, 80), readAt: 7)).Should().BeTrue(
            "the record keeps the later read, which a read between the two must not pass");
    }

    [Fact]
    public void Reads_are_handed_out_with_an_instant_that_never_goes_back()
    {
        var clock = new SteppingClock(At);
        var decisions = new AlertRuleDecisions(clock);

        var first = decisions.BeginRead();
        clock.Now = At.AddSeconds(2);
        var advanced = decisions.BeginRead();
        clock.Now = At.AddSeconds(-30);
        var steppedBack = decisions.BeginRead();

        advanced.Sequence.Should().BeGreaterThan(first.Sequence);
        advanced.Now.Should().Be(At.AddSeconds(2));
        steppedBack.Sequence.Should().BeGreaterThan(advanced.Sequence);
        steppedBack.Now.Should().Be(At.AddSeconds(2), "a later read never reads as of an earlier instant");
    }

    [Fact]
    public async Task A_newer_pass_over_other_rules_does_not_skip_the_older_passs_glucose_rule()
    {
        var orchestrator = Orchestrator();
        var pumpRule = Rule("reservoir");
        var passA = await HoldPassAAsync(orchestrator, Reading(0, 80));

        await PassAsync(orchestrator, Reading(5, 52), _decisions.BeginRead().Sequence, pumpRule).WaitAsync(Eventually.DefaultPatience);
        await ReleaseAsync(passA);

        _evaluated.Should().Equal((First.Id, 80m), (pumpRule.Id, 52m), (Second.Id, 80m), (Third.Id, 80m));
    }

    [Fact]
    public async Task A_correction_at_the_same_timestamp_read_later_decides_the_rule()
    {
        var orchestrator = Orchestrator();
        var passA = await HoldPassAAsync(orchestrator, Reading(0, 80));

        await PassAsync(orchestrator, Reading(0, 52), _decisions.BeginRead().Sequence, Second).WaitAsync(Eventually.DefaultPatience);
        await ReleaseAsync(passA);

        _evaluated.Should().Equal((First.Id, 80m), (Second.Id, 52m), (Third.Id, 80m));
    }

    [Fact]
    public async Task The_same_reading_never_supersedes_itself()
    {
        var orchestrator = Orchestrator();
        var reading = Reading(0, 80);
        _firstDeliveryReleased.SetResult();

        await PassAsync(orchestrator, reading, _decisions.BeginRead().Sequence, First, Second).WaitAsync(Eventually.DefaultPatience);
        await PassAsync(orchestrator, reading, _decisions.BeginRead().Sequence, First, Second).WaitAsync(Eventually.DefaultPatience);

        _evaluated.Should().HaveCount(4);
    }

    [Fact]
    public void An_older_reading_read_before_the_newer_was_decided_is_superseded_and_does_not_supersede_it()
    {
        var older = Stamp(Reading(0, 80), _decisions.BeginRead().Sequence);
        var newer = Stamp(Reading(5, 52), _decisions.BeginRead().Sequence);
        _decisions.Decided(First.Id, newer);

        _decisions.DecidedByNewer(First.Id, older).Should().BeTrue();
        _decisions.Decided(Second.Id, older);
        _decisions.DecidedByNewer(Second.Id, newer).Should().BeFalse();
    }

    [Fact]
    public void A_read_begun_after_the_newest_decision_that_finds_an_older_reading_is_newer()
    {
        var deleted = Stamp(Reading(5, 52), _decisions.BeginRead().Sequence);
        _decisions.Decided(First.Id, deleted);
        var remaining = Stamp(Reading(0, 80), _decisions.BeginRead().Sequence);

        _decisions.DecidedByNewer(First.Id, remaining).Should().BeFalse();
        _decisions.Decided(First.Id, remaining);
        _decisions.DecidedByNewer(First.Id, deleted).Should().BeTrue();
    }

    [Fact]
    public async Task An_unordered_pass_is_neither_skipped_nor_recorded()
    {
        var orchestrator = Orchestrator();
        _firstDeliveryReleased.SetResult();
        var readByWrite = _decisions.BeginRead().Sequence;
        await PassAsync(orchestrator, Reading(5, 52), _decisions.BeginRead().Sequence, Second).WaitAsync(Eventually.DefaultPatience);

        await orchestrator.EvaluateRulesAsync(
            [Second], new HashSet<Guid> { Second.Id }, CanonicalAlertEvaluator.ContextFor(Reading(0, 80), default),
            readAt: null, CancellationToken.None).WaitAsync(Eventually.DefaultPatience);
        await PassAsync(orchestrator, Reading(5, 52), readByWrite, Second).WaitAsync(Eventually.DefaultPatience);

        _evaluated.Should().Equal((Second.Id, 52m), (Second.Id, 80m), (Second.Id, 52m));
    }

    [Fact]
    public void A_context_without_a_reading_time_is_never_ordered()
    {
        AlertRuleDecisions.StampOf(new SensorContext { LatestValue = null, LatestTimestamp = null, TrendRate = null, LastReadingAt = null }, _decisions.BeginRead().Sequence)
            .Should().BeNull();
    }

    /// <summary>A wall clock a test can step back, as an NTP correction does.</summary>
    private sealed class SteppingClock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private static ContextStamp Stamp(SensorGlucose reading, long readAt) =>
        AlertRuleDecisions.StampOf(CanonicalAlertEvaluator.ContextFor(reading, default), readAt)!.Value;
}
