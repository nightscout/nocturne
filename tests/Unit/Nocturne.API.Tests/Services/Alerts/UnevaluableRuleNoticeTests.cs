using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// A rule whose stored body cannot be evaluated tells its recipients once per version of its
/// condition, through its own channels and behind the Do Not Disturb gate its excursions pass, and
/// the notice is resolved once the rule can run again or is retired. The store is shared between
/// harnesses, so a harness with a fresh <see cref="ConditionVersionLog"/> is a restart, and two at
/// once are two instances. A dispatch writes a delivery row, as the real one does, since that row
/// is what records that the notice was sent.
/// </summary>
[Trait("Category", "Unit")]
public class UnevaluableRuleNoticeTests
{
    private static readonly Guid Tenant = Guid.Parse("36003600-0000-7000-8000-0000000000aa");
    private static readonly Guid RuleId = Guid.Parse("36003600-0000-7000-8000-0000000000a1");
    private static readonly Guid OtherRuleId = Guid.Parse("36003600-0000-7000-8000-0000000000a2");
    private static readonly Guid FailsDelivery = Guid.Parse("36003600-0000-7000-8000-0000000000a3");
    private static readonly DateTime Now = new(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);

    private const string Broken = """{"threshold": 54, "direction": "below"}""";
    private const string OtherBroken = """{"threshold": 55, "direction": "below"}""";
    private const string Valid = """{"direction": "below", "value": 54}""";

    private static readonly RustValidationIssue BodyIssue = new("condition", "threshold", "field_missing", "value");

    private readonly NocturneDbContext _db = new(
        new DbContextOptionsBuilder<NocturneDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options)
    {
        TenantId = Tenant,
    };

    private readonly Mock<IAlertDeliveryService> _delivery = new();
    private readonly Mock<IExcursionResolutionHandler> _resolution = new();
    private readonly Mock<IAlertRuleConditionValidator> _validator = new();
    private readonly Mock<ISensorContextEnricher> _enricher = new();
    private IReadOnlySet<DndScope> _dnd = new HashSet<DndScope>();

    public UnevaluableRuleNoticeTests()
    {
        foreach (var id in new[] { RuleId, OtherRuleId, FailsDelivery })
        {
            _db.AlertRules.Add(new AlertRuleEntity
            {
                Id = id,
                TenantId = Tenant,
                Name = "Urgent Low",
                ConditionType = AlertConditionType.Threshold,
                ConditionParams = Broken,
                Severity = AlertRuleSeverity.Warning,
                IsEnabled = true,
                CreatedAt = Now,
                UpdatedAt = Now,
            });
        }
        _db.SaveChanges();

        _validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns([]);
        BodyCannotBeEvaluated(Broken);
        BodyCannotBeEvaluated(OtherBroken);

        _delivery
            .Setup(d => d.DispatchAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
                It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()))
            .Returns((Guid instanceId, IReadOnlyList<AlertRuleChannelSnapshot> _, AlertPayload _, CancellationToken ct) =>
                RecordDeliveryAsync(instanceId, ct));

        _enricher
            .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SensorContext c, IEnumerable<AlertRuleSnapshot> _, CancellationToken _) =>
                c with { ActiveDndScopes = _dnd });

        _resolution
            .Setup(h => h.HandleClosedAsync(It.IsAny<ExcursionTransition>(), Tenant, It.IsAny<CancellationToken>()))
            .Returns(async (ExcursionTransition t, Guid _, CancellationToken ct) =>
            {
                await using var db = await new TestNocturneDbContextFactory(_db).CreateAsync(ct);
                foreach (var instance in db.AlertInstances.Where(i => i.AlertExcursionId == t.ExcursionId))
                    instance.ResolvedAt = Now;
                await db.SaveChangesAsync(ct);
            });
    }

    private async Task RecordDeliveryAsync(Guid instanceId, CancellationToken ct)
    {
        await using var db = await new TestNocturneDbContextFactory(_db).CreateAsync(ct);
        db.AlertDeliveries.Add(new AlertDeliveryEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = Tenant,
            AlertInstanceId = instanceId,
            ChannelType = ChannelType.Webhook,
            Destination = "https://example.test/hook",
            Payload = "{}",
            Status = "delivered",
            CreatedAt = Now,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task SetEnabledAsync(Guid ruleId, bool enabled)
    {
        await using var db = await new TestNocturneDbContextFactory(_db).CreateAsync(CancellationToken.None);
        (await db.AlertRules.SingleAsync(r => r.Id == ruleId)).IsEnabled = enabled;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The repository, over a store whose rule holds the condition the run read unless
    /// <paramref name="storeHoldsWhatTheRunRead"/> is false, so a test changes a rule's condition by
    /// handing the run a new snapshot, as an edit before the run would.
    /// </summary>
    private sealed class CountingRepository(INocturneDbContextFactory contexts, bool storeHoldsWhatTheRunRead = true)
        : AlertRepository(contexts)
    {
        public int Records { get; private set; }

        public override async Task<NoticeRecorded?> RecordUnevaluableNoticeAsync(
            AlertRuleSnapshot rule, string conditionVersion, DateTime at, CancellationToken ct)
        {
            Records++;
            if (storeHoldsWhatTheRunRead)
            {
                await using var db = await contexts.CreateAsync(ct);
                var stored = await db.AlertRules.SingleAsync(r => r.Id == rule.Id, ct);
                (stored.ConditionType, stored.ConditionParams) = (rule.ConditionType, rule.ConditionParams);
                await db.SaveChangesAsync(ct);
            }
            return await base.RecordUnevaluableNoticeAsync(rule, conditionVersion, at, ct);
        }
    }

    private void BodyCannotBeEvaluated(string body) =>
        _validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), body, It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns([BodyIssue]);

    private static AlertRuleSnapshot Rule(
        string body, AlertRuleSeverity severity = AlertRuleSeverity.Warning, Guid? id = null) =>
        new(id ?? RuleId, Tenant, "Urgent Low", AlertConditionType.Threshold, body,
            severity, "{}", 0, false, null);

    private static EnabledAlertRules Enabled(params AlertRuleSnapshot[] rules) => new(rules, []);

    private static readonly AlertRuleChannelSnapshot Webhook =
        new(Guid.Parse("36003600-0000-7000-8000-0000000000c1"), RuleId, ChannelType.Webhook, "https://example.test/hook", null, 0);

    private UnevaluableRuleNotice Process(ConditionVersionLog? log = null, AlertRepository? repository = null)
    {
        var contexts = new TestNocturneDbContextFactory(_db);
        var tenant = new Mock<ITenantAccessor>();
        tenant.SetupGet(t => t.TenantId).Returns(Tenant);

        var rules = new Mock<IAlertRuleRepository>();
        rules
            .Setup(r => r.GetChannelsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                ids.ToDictionary(id => id, IReadOnlyList<AlertRuleChannelSnapshot> (id) => [Webhook with { AlertRuleId = id }]));

        return new UnevaluableRuleNotice(
            repository ?? new CountingRepository(contexts),
            rules.Object,
            tenant.Object,
            _delivery.Object,
            _enricher.Object,
            _resolution.Object,
            log ?? new ConditionVersionLog(),
            _validator.Object,
            new FakeTimeProvider(new DateTimeOffset(Now)),
            new ListLogger<UnevaluableRuleNotice>());
    }

    private void VerifySent(Times times, Guid? ruleId = null, string because = "") =>
        _delivery.Verify(d => d.DispatchAsync(
                It.IsAny<Guid>(),
                It.Is<IReadOnlyList<AlertRuleChannelSnapshot>>(c => c.Single().AlertRuleId == (ruleId ?? RuleId)),
                It.Is<AlertPayload>(p => p.Kind == AlertPayloadKind.Unevaluable && p.GlucoseValue == null),
                It.IsAny<CancellationToken>()),
            times, because);

    private Task<List<Guid>> StandingAsync() =>
        _db.AlertExcursions.AsNoTracking()
            .Where(e => e.UnevaluableConditionVersion != null && e.Instances.Any(i => i.ResolvedAt == null))
            .Select(e => e.Id)
            .ToListAsync();

    private void VerifyResolved(Guid excursionId, ExcursionCloseReason reason) =>
        _resolution.Verify(h => h.HandleClosedAsync(
                It.Is<ExcursionTransition>(t => t.ExcursionId == excursionId
                                                && t.Type == ExcursionTransitionType.ExcursionClosed
                                                && t.CloseReason == reason),
                Tenant, It.IsAny<CancellationToken>()),
            Times.Once);

    [Fact]
    public async Task An_enabled_rule_whose_body_cannot_be_evaluated_tells_its_channels_once()
    {
        var process = Process();

        await process.CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);
        await process.CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);

        VerifySent(Times.Once());
        _delivery.Verify(d => d.DispatchAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
            It.Is<AlertPayload>(p => p.RuleName == "Urgent Low" && p.Severity == AlertRuleSeverity.Warning
                                     && p.Trend == null && p.TrendRate == null),
            It.IsAny<CancellationToken>()), Times.Once);
        (await StandingAsync()).Should().ContainSingle();
        (await _db.AlertExcursions.SingleAsync()).EndedAt.Should().Be(AlertExcursionEntity.NoticeStandingEnd,
            "a standing notice is never an open excursion, and never one the history clamp hides");
    }

    [Fact]
    public async Task A_disabled_rule_whose_body_cannot_be_evaluated_is_not_told()
    {
        await SetEnabledAsync(RuleId, false);
        await SetEnabledAsync(FailsDelivery, false);
        var enabledRules = await new AlertRepository(new TestNocturneDbContextFactory(_db))
            .GetEnabledRulesAsync(CancellationToken.None);

        await Process().CheckAsync(new EnabledAlertRules(enabledRules, []), CancellationToken.None);

        VerifySent(Times.Never(), RuleId);
        VerifySent(Times.Once(), OtherRuleId);
    }

    [Fact]
    public async Task A_restart_or_a_second_instance_does_not_send_it_again()
    {
        await Process().CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);

        var restarted = Process();
        var secondInstance = Process();
        await restarted.CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);
        await secondInstance.CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);

        VerifySent(Times.Once());
        (await _db.AlertExcursions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_edit_to_another_condition_that_cannot_be_evaluated_sends_it_again()
    {
        var process = Process();
        await process.CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);
        var first = (await StandingAsync()).Single();

        await process.CheckAsync(Enabled(Rule(OtherBroken)), CancellationToken.None);

        VerifySent(Times.Exactly(2));
        VerifyResolved(first, ExcursionCloseReason.Manual);
        (await StandingAsync()).Should().ContainSingle().Which.Should().NotBe(first);
    }

    [Fact]
    public async Task An_edit_to_a_condition_that_can_be_evaluated_resolves_it()
    {
        var process = Process();
        await process.CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);
        var notice = (await StandingAsync()).Single();

        await process.CheckAsync(Enabled(Rule(Valid)), CancellationToken.None);

        VerifyResolved(notice, ExcursionCloseReason.Manual);
        (await StandingAsync()).Should().BeEmpty();
        VerifySent(Times.Once());
    }

    [Fact]
    public async Task Retiring_the_rule_by_disabling_or_deleting_it_resolves_it()
    {
        await Process().CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);
        var notice = (await StandingAsync()).Single();
        var tracker = new Mock<IExcursionTracker>();
        tracker
            .Setup(t => t.ForceCloseAsync(RuleId, ExcursionCloseReason.RuleDisabled, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExcursionTransition(ExcursionTransitionType.None));

        await new AlertRuleRetirement(tracker.Object, _resolution.Object, Process())
            .CloseAsync([RuleId], Tenant, CancellationToken.None);

        VerifyResolved(notice, ExcursionCloseReason.RuleDisabled);
        (await StandingAsync()).Should().BeEmpty();
        (await _db.AlertExcursions.SingleAsync()).EndedAt.Should().Be(Now);
    }

    [Fact]
    public async Task A_rule_disabled_and_enabled_again_while_still_broken_is_told_again()
    {
        var log = new ConditionVersionLog();
        var tracker = new Mock<IExcursionTracker>();
        tracker
            .Setup(t => t.ForceCloseAsync(RuleId, ExcursionCloseReason.RuleDisabled, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExcursionTransition(ExcursionTransitionType.None));

        await Process(log).CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);
        await new AlertRuleRetirement(tracker.Object, _resolution.Object, Process(log))
            .CloseAsync([RuleId], Tenant, CancellationToken.None);
        await Process(log).CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);

        VerifySent(Times.Exactly(2));
        (await StandingAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_rule_disabled_through_another_instance_and_enabled_again_is_told_again()
    {
        var thisInstance = Process();
        await thisInstance.CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);
        await Process().ResolveAsync(RuleId, ExcursionCloseReason.RuleDisabled, CancellationToken.None);

        await thisInstance.CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);

        VerifySent(Times.Exactly(2));
    }

    [Fact]
    public async Task A_notice_whose_dispatch_failed_after_its_row_was_written_is_sent_by_a_later_pass()
    {
        var calls = 0;
        _enricher
            .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SensorContext c, IEnumerable<AlertRuleSnapshot> _, CancellationToken _) =>
                ++calls == 1 ? throw new TimeoutException("enricher timed out") : c);
        var process = Process();

        await process.CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);
        VerifySent(Times.Never());
        (await StandingAsync()).Should().ContainSingle();

        await process.CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);
        await process.CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);

        VerifySent(Times.Once());
        (await _db.AlertExcursions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_notice_whose_dispatch_was_cancelled_is_sent_after_a_restart()
    {
        var dispatches = 0;
        using var shutdown = new CancellationTokenSource();
        _delivery
            .Setup(d => d.DispatchAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
                It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()))
            .Returns((Guid instanceId, IReadOnlyList<AlertRuleChannelSnapshot> _, AlertPayload _, CancellationToken ct) =>
            {
                if (++dispatches > 1)
                    return RecordDeliveryAsync(instanceId, ct);
                shutdown.Cancel();
                throw new OperationCanceledException(shutdown.Token);
            });

        var cancelled = () => Process().CheckAsync(Enabled(Rule(Broken)), shutdown.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        await Process().CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);
        await Process().CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);

        VerifySent(Times.Exactly(2));
        (await _db.AlertDeliveries.CountAsync()).Should().Be(1, "one dispatch completed for the version");
    }

    [Fact]
    public async Task A_notice_that_do_not_disturb_silenced_is_not_sent_by_a_later_pass()
    {
        _dnd = new HashSet<DndScope> { DndScope.All };
        await Process().CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);

        _dnd = new HashSet<DndScope>();
        await Process().CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);

        VerifySent(Times.Never());
    }

    [Fact]
    public async Task Reporting_for_evaluation_logs_once_and_sends_nothing()
    {
        var logger = new ListLogger<UnevaluableRuleNotice>();
        var log = new ConditionVersionLog();
        var notice = new UnevaluableRuleNotice(
            Mock.Of<IAlertRepository>(MockBehavior.Strict),
            Mock.Of<IAlertRuleRepository>(MockBehavior.Strict),
            Mock.Of<ITenantAccessor>(t => t.TenantId == Tenant),
            Mock.Of<IAlertDeliveryService>(MockBehavior.Strict),
            Mock.Of<ISensorContextEnricher>(MockBehavior.Strict),
            Mock.Of<IExcursionResolutionHandler>(MockBehavior.Strict),
            log,
            _validator.Object,
            new FakeTimeProvider(new DateTimeOffset(Now)),
            logger);

        notice.Report([Rule(Broken)]);
        notice.Report([Rule(Broken)]);

        logger.Warnings.Should().ContainSingle();
        await Process(log).CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);
        VerifySent(Times.Once(), because: "the sweep still sends a version that evaluation logged first");
    }

    [Theory]
    [InlineData("auto_resolve")]
    [InlineData("snooze")]
    public async Task A_tree_other_than_the_body_that_cannot_be_evaluated_sends_nothing(string scope)
    {
        _validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), Valid, It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns([new RustValidationIssue(scope, scope, "field_missing", "value")]);

        await Process().CheckAsync(Enabled(Rule(Valid)), CancellationToken.None);

        _delivery.VerifyNoOtherCalls();
        (await _db.AlertExcursions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Do_not_disturb_silences_a_warning_rules_notice_as_it_would_its_excursion()
    {
        _dnd = new HashSet<DndScope> { DndScope.All };

        await Process().CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);

        _delivery.VerifyNoOtherCalls();
        (await _db.AlertInstances.SingleAsync()).SuppressionReason.Should().Be("dnd:all");
    }

    [Fact]
    public async Task Do_not_disturb_does_not_silence_a_critical_rules_notice()
    {
        _dnd = new HashSet<DndScope> { DndScope.All };

        await Process().CheckAsync(Enabled(Rule(Broken, AlertRuleSeverity.Critical)), CancellationToken.None);

        VerifySent(Times.Once());
        (await _db.AlertInstances.SingleAsync()).SuppressionReason.Should().BeNull();
    }

    [Fact]
    public async Task A_failing_check_or_delivery_for_one_rule_does_not_stop_another()
    {
        const string throwsOnCheck = """{"threshold": 1}""";
        _validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), throwsOnCheck, It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .Throws(new RustAlertEngineException("validator failed"));
        var failsDelivery = FailsDelivery;
        _delivery
            .Setup(d => d.DispatchAsync(It.IsAny<Guid>(),
                It.Is<IReadOnlyList<AlertRuleChannelSnapshot>>(c => c.Single().AlertRuleId == failsDelivery),
                It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("delivery failed"));

        await Process().CheckAsync(
            Enabled(Rule(throwsOnCheck), Rule(Broken, id: failsDelivery), Rule(Broken, id: OtherRuleId)),
            CancellationToken.None);

        VerifySent(Times.Once(), OtherRuleId);
    }

    [Fact]
    public async Task A_notice_recorded_as_a_disable_lands_is_resolved_by_the_next_run_and_told_again_on_enable()
    {
        await Process().CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);
        var notice = (await StandingAsync()).Single();
        await SetEnabledAsync(RuleId, false);

        await Process().CheckAsync(Enabled(), CancellationToken.None);

        VerifyResolved(notice, ExcursionCloseReason.RuleDisabled);
        (await StandingAsync()).Should().BeEmpty();

        await SetEnabledAsync(RuleId, true);
        await Process().CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);
        VerifySent(Times.Exactly(2), because: "a rule enabled again while still broken is told again");
    }

    [Fact]
    public async Task A_run_that_read_a_condition_the_rule_no_longer_has_records_nothing()
    {
        var process = Process(repository: new CountingRepository(new TestNocturneDbContextFactory(_db), storeHoldsWhatTheRunRead: false));

        await process.CheckAsync(Enabled(Rule(OtherBroken)), CancellationToken.None);

        VerifySent(Times.Never(), because: "the store holds Broken; OtherBroken was edited away before the run recorded it");
        (await _db.AlertExcursions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_run_that_read_the_rule_before_it_was_disabled_records_nothing()
    {
        var readBeforeTheDisable = Rule(Broken);
        await SetEnabledAsync(RuleId, false);

        await Process().CheckAsync(Enabled(readBeforeTheDisable), CancellationToken.None);

        VerifySent(Times.Never());
        (await _db.AlertExcursions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_notice_whose_retirement_failed_is_resolved_by_the_next_run()
    {
        await Process().CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);
        var notice = (await StandingAsync()).Single();
        _resolution
            .Setup(h => h.HandleClosedAsync(It.IsAny<ExcursionTransition>(), Tenant, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("resolution failed"));
        await SetEnabledAsync(RuleId, false);
        var retire = () => Process().ResolveAsync(RuleId, ExcursionCloseReason.RuleDisabled, CancellationToken.None);
        await retire.Should().ThrowAsync<TimeoutException>();

        _resolution
            .Setup(h => h.HandleClosedAsync(It.IsAny<ExcursionTransition>(), Tenant, It.IsAny<CancellationToken>()))
            .Returns(async (ExcursionTransition t, Guid _, CancellationToken ct) =>
            {
                await using var db = await new TestNocturneDbContextFactory(_db).CreateAsync(ct);
                foreach (var instance in db.AlertInstances.Where(i => i.AlertExcursionId == t.ExcursionId))
                    instance.ResolvedAt = Now;
                await db.SaveChangesAsync(ct);
            });
        await Process().CheckAsync(Enabled(), CancellationToken.None);

        (await StandingAsync()).Should().BeEmpty();
        _resolution.Verify(h => h.HandleClosedAsync(
            It.Is<ExcursionTransition>(t => t.ExcursionId == notice && t.CloseReason == ExcursionCloseReason.RuleDisabled),
            Tenant, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task A_superseded_notice_that_cannot_be_resolved_does_not_hold_back_the_new_one()
    {
        await Process().CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);
        var first = (await StandingAsync()).Single();
        _resolution
            .Setup(h => h.HandleClosedAsync(It.Is<ExcursionTransition>(t => t.ExcursionId == first), Tenant, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("resolution failed"));

        await Process().CheckAsync(Enabled(Rule(OtherBroken)), CancellationToken.None);

        VerifySent(Times.Exactly(2));
    }

    [Fact]
    public async Task A_dispatched_notice_is_not_recorded_again_by_later_runs()
    {
        var repository = new CountingRepository(new TestNocturneDbContextFactory(_db));
        var process = Process(repository: repository);

        await process.CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);
        await process.CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);
        await process.CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);

        repository.Records.Should().Be(1, "a run that finds the notice dispatched opens no write transaction");
        VerifySent(Times.Once());
    }

    [Fact]
    public async Task A_retirement_during_the_dispatch_is_resolved_again_after_it()
    {
        _delivery
            .Setup(d => d.DispatchAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
                It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid instanceId, IReadOnlyList<AlertRuleChannelSnapshot> _, AlertPayload _, CancellationToken ct) =>
            {
                await Process().ResolveAsync(RuleId, ExcursionCloseReason.RuleDisabled, ct);
                await RecordDeliveryAsync(instanceId, ct);
            });

        await Process().CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);

        var notice = await _db.AlertExcursions.SingleAsync();
        _resolution.Verify(h => h.HandleClosedAsync(
            It.Is<ExcursionTransition>(t => t.ExcursionId == notice.Id && t.CloseReason == ExcursionCloseReason.RuleDisabled),
            Tenant, It.IsAny<CancellationToken>()), Times.Exactly(2),
            "what the provider made after the retirement (an in-app notification) is cleared too");
    }

    [Fact]
    public async Task A_notice_of_a_rule_that_can_be_evaluated_again_is_resolved_by_a_later_run_when_the_first_try_fails()
    {
        await Process().CheckAsync(Enabled(Rule(Broken)), CancellationToken.None);
        var notice = (await StandingAsync()).Single();
        var failing = true;
        _resolution
            .Setup(h => h.HandleClosedAsync(It.IsAny<ExcursionTransition>(), Tenant, It.IsAny<CancellationToken>()))
            .Returns(async (ExcursionTransition t, Guid _, CancellationToken ct) =>
            {
                if (failing) throw new TimeoutException("resolution failed");
                await using var db = await new TestNocturneDbContextFactory(_db).CreateAsync(ct);
                foreach (var instance in db.AlertInstances.Where(i => i.AlertExcursionId == t.ExcursionId))
                    instance.ResolvedAt = Now;
                await db.SaveChangesAsync(ct);
            });
        var log = new ConditionVersionLog();

        await Process(log).CheckAsync(Enabled(Rule(Valid)), CancellationToken.None);
        failing = false;
        await Process(log).CheckAsync(Enabled(Rule(Valid)), CancellationToken.None);

        (await StandingAsync()).Should().BeEmpty();
        notice.Should().NotBeEmpty();
    }
}
