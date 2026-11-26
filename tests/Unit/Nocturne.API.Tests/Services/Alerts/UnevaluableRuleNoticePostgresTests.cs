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
/// the processes of a run, so a process with a fresh <see cref="ConditionVersionLog"/> is a restart,
/// and two at once are two instances. A dispatch writes a delivery row, as the real one does, since
/// that row is what records that the notice was sent. On PostgreSQL under the runtime role, since
/// the notice rows are read and settled by statements the in-memory store cannot run.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UnevaluableRuleNoticePostgresTests(UnevaluableRuleNoticePostgresTests.Database database)
    : IClassFixture<UnevaluableRuleNoticePostgresTests.Database>
{
    private static readonly DateTime Now = new(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);

    private const string Broken = """{"threshold": 54, "direction": "below"}""";
    private const string OtherBroken = """{"threshold": 55, "direction": "below"}""";
    private const string Valid = """{"direction": "below", "value": 54}""";

    private static readonly RustValidationIssue BodyIssue = new("condition", "threshold", "field_missing", "value");

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var created = await SharedPostgres.CreateMigratedDatabaseAsync("unevaluable_rule_notice");
            AppConnectionString = created.AppConnectionString;
            MigratorConnectionString = created.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
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

    /// <summary>One tenant with three enabled rules whose body cannot be evaluated, and the pieces of the processes over its store.</summary>
    private sealed class Run
    {
        public required Guid Tenant { get; init; }
        public required Func<NocturneDbContext> Context { get; init; }
        public Guid RuleId { get; } = Guid.CreateVersion7();
        public Guid OtherRuleId { get; } = Guid.CreateVersion7();
        public Guid FailsDelivery { get; } = Guid.CreateVersion7();
        public Mock<IAlertDeliveryService> Delivery { get; } = new();
        public Mock<IExcursionResolutionHandler> Resolution { get; } = new();
        public Mock<IAlertRuleConditionValidator> Validator { get; } = new();
        public Mock<ISensorContextEnricher> Enricher { get; } = new();
        public IReadOnlySet<DndScope> Dnd { get; set; } = new HashSet<DndScope>();

        public AlertRepository Repository() => new(new SuppliedContextFactory(Context));

        public CountingRepository Counting(bool storeHoldsWhatTheRunRead = true) =>
            new(new SuppliedContextFactory(Context), storeHoldsWhatTheRunRead);

        public void ConfigureMocks()
        {
            Validator
                .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(),
                    It.IsAny<string?>(), It.IsAny<string?>()))
                .Returns([]);
            BodyCannotBeEvaluated(Broken);
            BodyCannotBeEvaluated(OtherBroken);

            Delivery
                .Setup(d => d.DispatchAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
                    It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()))
                .Returns((Guid instanceId, IReadOnlyList<AlertRuleChannelSnapshot> _, AlertPayload _, CancellationToken ct) =>
                    RecordDeliveryAsync(instanceId, ct));

            Enricher
                .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SensorContext c, IEnumerable<AlertRuleSnapshot> _, CancellationToken _) =>
                    c with { ActiveDndScopes = Dnd });

            ResolutionResolvesInstances();
        }

        public void ResolutionResolvesInstances() =>
            Resolution
                .Setup(h => h.HandleClosedAsync(It.IsAny<ExcursionTransition>(), Tenant, It.IsAny<CancellationToken>()))
                .Returns((ExcursionTransition t, Guid _, CancellationToken ct) => ResolveInstancesAsync(t, ct));

        public void BodyCannotBeEvaluated(string body) =>
            Validator
                .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), body, It.IsAny<bool>(),
                    It.IsAny<string?>(), It.IsAny<string?>()))
                .Returns([BodyIssue]);

        public async Task ResolveInstancesAsync(ExcursionTransition transition, CancellationToken ct)
        {
            await using var db = Context();
            foreach (var instance in await db.AlertInstances.Where(i => i.AlertExcursionId == transition.ExcursionId).ToListAsync(ct))
                instance.ResolvedAt = Now;
            await db.SaveChangesAsync(ct);
        }

        public async Task RecordDeliveryAsync(Guid instanceId, CancellationToken ct)
        {
            await using var db = Context();
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

        public async Task SetEnabledAsync(Guid ruleId, bool enabled)
        {
            await using var db = Context();
            (await db.AlertRules.SingleAsync(r => r.Id == ruleId)).IsEnabled = enabled;
            await db.SaveChangesAsync();
        }

        public AlertRuleSnapshot Rule(string body, AlertRuleSeverity severity = AlertRuleSeverity.Warning, Guid? id = null) =>
            new(id ?? RuleId, Tenant, "Urgent Low", AlertConditionType.Threshold, body,
                severity, "{}", 0, false, null);

        public UnevaluableRuleNotice Process(ConditionVersionLog? log = null, AlertRepository? repository = null)
        {
            var tenant = new Mock<ITenantAccessor>();
            tenant.SetupGet(t => t.TenantId).Returns(Tenant);

            var rules = new Mock<IAlertRuleRepository>();
            rules
                .Setup(r => r.GetChannelsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                    ids.ToDictionary(id => id, IReadOnlyList<AlertRuleChannelSnapshot> (id) =>
                        [new AlertRuleChannelSnapshot(Guid.CreateVersion7(), id, ChannelType.Webhook, "https://example.test/hook", null, 0)]));

            return new UnevaluableRuleNotice(
                repository ?? Counting(),
                rules.Object,
                tenant.Object,
                Delivery.Object,
                Enricher.Object,
                Resolution.Object,
                log ?? new ConditionVersionLog(),
                Validator.Object,
                new FakeTimeProvider(new DateTimeOffset(Now)),
                new ListLogger<UnevaluableRuleNotice>());
        }

        public void VerifySent(Times times, Guid? ruleId = null, string because = "") =>
            Delivery.Verify(d => d.DispatchAsync(
                    It.IsAny<Guid>(),
                    It.Is<IReadOnlyList<AlertRuleChannelSnapshot>>(c => c.Single().AlertRuleId == (ruleId ?? RuleId)),
                    It.Is<AlertPayload>(p => p.Kind == AlertPayloadKind.Unevaluable && p.GlucoseValue == null),
                    It.IsAny<CancellationToken>()),
                times, because);

        public async Task<List<Guid>> StandingAsync()
        {
            await using var db = Context();
            return await db.AlertExcursions.AsNoTracking()
                .Where(e => e.UnevaluableConditionVersion != null && e.Instances.Any(i => i.ResolvedAt == null))
                .Select(e => e.Id)
                .ToListAsync();
        }

        public void VerifyResolved(Guid excursionId, ExcursionCloseReason reason) =>
            Resolution.Verify(h => h.HandleClosedAsync(
                    It.Is<ExcursionTransition>(t => t.ExcursionId == excursionId
                                                    && t.Type == ExcursionTransitionType.ExcursionClosed
                                                    && t.CloseReason == reason),
                    Tenant, It.IsAny<CancellationToken>()),
                Times.Once);

        public async Task<List<AlertExcursionEntity>> ExcursionsAsync()
        {
            await using var db = Context();
            return await db.AlertExcursions.AsNoTracking().ToListAsync();
        }

        public async Task<List<AlertInstanceEntity>> InstancesAsync()
        {
            await using var db = Context();
            return await db.AlertInstances.AsNoTracking().ToListAsync();
        }

        public async Task<int> DeliveryCountAsync()
        {
            await using var db = Context();
            return await db.AlertDeliveries.CountAsync();
        }
    }

    private async Task<Run> SeedAsync()
    {
        var tenant = Guid.NewGuid();
        await NpgsqlTestCommands.SeedTenantAsync(
            database.MigratorConnectionString, tenant, $"unevaluable-{tenant:N}", "unevaluable-rule-notice");

        var run = new Run
        {
            Tenant = tenant,
            Context = () => TestContexts.Context(database.AppConnectionString, tenant),
        };
        await using (var seed = run.Context())
        {
            foreach (var id in new[] { run.RuleId, run.OtherRuleId, run.FailsDelivery })
            {
                seed.AlertRules.Add(new AlertRuleEntity
                {
                    Id = id,
                    TenantId = tenant,
                    Name = "Urgent Low",
                    ConditionType = AlertConditionType.Threshold,
                    ConditionParams = Broken,
                    Severity = AlertRuleSeverity.Warning,
                    IsEnabled = true,
                    CreatedAt = Now,
                    UpdatedAt = Now,
                });
            }
            await seed.SaveChangesAsync();
        }

        run.ConfigureMocks();
        return run;
    }

    private static EnabledAlertRules Enabled(params AlertRuleSnapshot[] rules) => new(rules, []);

    [Fact]
    public async Task An_enabled_rule_whose_body_cannot_be_evaluated_tells_its_channels_once()
    {
        var run = await SeedAsync();
        var process = run.Process();

        await process.CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);
        await process.CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);

        run.VerifySent(Times.Once());
        run.Delivery.Verify(d => d.DispatchAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
            It.Is<AlertPayload>(p => p.RuleName == "Urgent Low" && p.Severity == AlertRuleSeverity.Warning
                                     && p.Trend == null && p.TrendRate == null),
            It.IsAny<CancellationToken>()), Times.Once);
        (await run.StandingAsync()).Should().ContainSingle();
        (await run.ExcursionsAsync()).Should().ContainSingle().Which.EndedAt.Should().Be(AlertExcursionEntity.NoticeStandingEnd,
            "a standing notice is never an open excursion, and never one the history clamp hides");
    }

    [Fact]
    public async Task A_disabled_rule_whose_body_cannot_be_evaluated_is_not_told()
    {
        var run = await SeedAsync();
        await run.SetEnabledAsync(run.RuleId, false);
        await run.SetEnabledAsync(run.FailsDelivery, false);
        var enabledRules = await run.Repository().GetEnabledRulesAsync(CancellationToken.None);
        foreach (var rule in enabledRules)
            run.BodyCannotBeEvaluated(rule.ConditionParams); // jsonb hands the body back in its own key order

        await run.Process().CheckAsync(new EnabledAlertRules(enabledRules, []), CancellationToken.None);

        run.VerifySent(Times.Never(), run.RuleId);
        run.VerifySent(Times.Once(), run.OtherRuleId);
    }

    [Fact]
    public async Task A_restart_or_a_second_instance_does_not_send_it_again()
    {
        var run = await SeedAsync();
        await run.Process().CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);

        var restarted = run.Process();
        var secondInstance = run.Process();
        await restarted.CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);
        await secondInstance.CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);

        run.VerifySent(Times.Once());
        (await run.ExcursionsAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task An_edit_to_another_condition_that_cannot_be_evaluated_sends_it_again()
    {
        var run = await SeedAsync();
        var process = run.Process();
        await process.CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);
        var first = (await run.StandingAsync()).Single();

        await process.CheckAsync(Enabled(run.Rule(OtherBroken)), CancellationToken.None);

        run.VerifySent(Times.Exactly(2));
        run.VerifyResolved(first, ExcursionCloseReason.Manual);
        (await run.StandingAsync()).Should().ContainSingle().Which.Should().NotBe(first);
    }

    [Fact]
    public async Task An_edit_to_a_condition_that_can_be_evaluated_resolves_it()
    {
        var run = await SeedAsync();
        var process = run.Process();
        await process.CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);
        var notice = (await run.StandingAsync()).Single();

        await process.CheckAsync(Enabled(run.Rule(Valid)), CancellationToken.None);

        run.VerifyResolved(notice, ExcursionCloseReason.Manual);
        (await run.StandingAsync()).Should().BeEmpty();
        run.VerifySent(Times.Once());
    }

    [Fact]
    public async Task Retiring_the_rule_by_disabling_or_deleting_it_resolves_it()
    {
        var run = await SeedAsync();
        await run.Process().CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);
        var notice = (await run.StandingAsync()).Single();
        var tracker = new Mock<IExcursionTracker>();
        tracker
            .Setup(t => t.ForceCloseAsync(run.RuleId, ExcursionCloseReason.RuleDisabled, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExcursionTransition(ExcursionTransitionType.None));

        await new AlertRuleRetirement(tracker.Object, run.Resolution.Object, run.Process())
            .CloseAsync([run.RuleId], run.Tenant, CancellationToken.None);

        run.VerifyResolved(notice, ExcursionCloseReason.RuleDisabled);
        (await run.StandingAsync()).Should().BeEmpty();
        (await run.ExcursionsAsync()).Should().ContainSingle().Which.EndedAt.Should().Be(Now);
    }

    [Fact]
    public async Task A_rule_disabled_and_enabled_again_while_still_broken_is_told_again()
    {
        var run = await SeedAsync();
        var log = new ConditionVersionLog();
        var tracker = new Mock<IExcursionTracker>();
        tracker
            .Setup(t => t.ForceCloseAsync(run.RuleId, ExcursionCloseReason.RuleDisabled, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExcursionTransition(ExcursionTransitionType.None));

        await run.Process(log).CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);
        await new AlertRuleRetirement(tracker.Object, run.Resolution.Object, run.Process(log))
            .CloseAsync([run.RuleId], run.Tenant, CancellationToken.None);
        await run.Process(log).CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);

        run.VerifySent(Times.Exactly(2));
        (await run.StandingAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_rule_disabled_through_another_instance_and_enabled_again_is_told_again()
    {
        var run = await SeedAsync();
        var thisInstance = run.Process();
        await thisInstance.CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);
        await run.Process().ResolveAsync(run.RuleId, ExcursionCloseReason.RuleDisabled, CancellationToken.None);

        await thisInstance.CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);

        run.VerifySent(Times.Exactly(2));
    }

    [Fact]
    public async Task A_notice_whose_dispatch_failed_after_its_row_was_written_is_sent_by_a_later_pass()
    {
        var run = await SeedAsync();
        var calls = 0;
        run.Enricher
            .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SensorContext c, IEnumerable<AlertRuleSnapshot> _, CancellationToken _) =>
                ++calls == 1 ? throw new TimeoutException("enricher timed out") : c);
        var process = run.Process();

        await process.CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);
        run.VerifySent(Times.Never());
        (await run.StandingAsync()).Should().ContainSingle();

        await process.CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);
        await process.CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);

        run.VerifySent(Times.Once());
        (await run.ExcursionsAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_notice_whose_dispatch_was_cancelled_is_sent_after_a_restart()
    {
        var run = await SeedAsync();
        var dispatches = 0;
        using var shutdown = new CancellationTokenSource();
        run.Delivery
            .Setup(d => d.DispatchAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
                It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()))
            .Returns((Guid instanceId, IReadOnlyList<AlertRuleChannelSnapshot> _, AlertPayload _, CancellationToken ct) =>
            {
                if (++dispatches > 1)
                    return run.RecordDeliveryAsync(instanceId, ct);
                shutdown.Cancel();
                throw new OperationCanceledException(shutdown.Token);
            });

        var cancelled = () => run.Process().CheckAsync(Enabled(run.Rule(Broken)), shutdown.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        await run.Process().CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);
        await run.Process().CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);

        run.VerifySent(Times.Exactly(2));
        (await run.DeliveryCountAsync()).Should().Be(1, "one dispatch completed for the version");
    }

    [Fact]
    public async Task A_notice_that_do_not_disturb_silenced_is_not_sent_by_a_later_pass()
    {
        var run = await SeedAsync();
        run.Dnd = new HashSet<DndScope> { DndScope.All };
        await run.Process().CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);

        run.Dnd = new HashSet<DndScope>();
        await run.Process().CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);

        run.VerifySent(Times.Never());
    }

    [Fact]
    public async Task Reporting_for_evaluation_logs_once_and_sends_nothing()
    {
        var run = await SeedAsync();
        var logger = new ListLogger<UnevaluableRuleNotice>();
        var log = new ConditionVersionLog();
        var notice = new UnevaluableRuleNotice(
            Mock.Of<IAlertRepository>(MockBehavior.Strict),
            Mock.Of<IAlertRuleRepository>(MockBehavior.Strict),
            Mock.Of<ITenantAccessor>(t => t.TenantId == run.Tenant),
            Mock.Of<IAlertDeliveryService>(MockBehavior.Strict),
            Mock.Of<ISensorContextEnricher>(MockBehavior.Strict),
            Mock.Of<IExcursionResolutionHandler>(MockBehavior.Strict),
            log,
            run.Validator.Object,
            new FakeTimeProvider(new DateTimeOffset(Now)),
            logger);

        notice.Report([run.Rule(Broken)]);
        notice.Report([run.Rule(Broken)]);

        logger.Warnings.Should().ContainSingle();
        await run.Process(log).CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);
        run.VerifySent(Times.Once(), because: "the sweep still sends a version that evaluation logged first");
    }

    [Theory]
    [InlineData("auto_resolve")]
    [InlineData("snooze")]
    public async Task A_tree_other_than_the_body_that_cannot_be_evaluated_sends_nothing(string scope)
    {
        var run = await SeedAsync();
        run.Validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), Valid, It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns([new RustValidationIssue(scope, scope, "field_missing", "value")]);

        await run.Process().CheckAsync(Enabled(run.Rule(Valid)), CancellationToken.None);

        run.Delivery.VerifyNoOtherCalls();
        (await run.ExcursionsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Do_not_disturb_silences_a_warning_rules_notice_as_it_would_its_excursion()
    {
        var run = await SeedAsync();
        run.Dnd = new HashSet<DndScope> { DndScope.All };

        await run.Process().CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);

        run.Delivery.VerifyNoOtherCalls();
        (await run.InstancesAsync()).Should().ContainSingle().Which.SuppressionReason.Should().Be("dnd:all");
    }

    [Fact]
    public async Task Do_not_disturb_does_not_silence_a_critical_rules_notice()
    {
        var run = await SeedAsync();
        run.Dnd = new HashSet<DndScope> { DndScope.All };

        await run.Process().CheckAsync(Enabled(run.Rule(Broken, AlertRuleSeverity.Critical)), CancellationToken.None);

        run.VerifySent(Times.Once());
        (await run.InstancesAsync()).Should().ContainSingle().Which.SuppressionReason.Should().BeNull();
    }

    [Fact]
    public async Task A_failing_check_or_delivery_for_one_rule_does_not_stop_another()
    {
        var run = await SeedAsync();
        const string throwsOnCheck = """{"threshold": 1}""";
        run.Validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), throwsOnCheck, It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .Throws(new RustAlertEngineException("validator failed"));
        var failsDelivery = run.FailsDelivery;
        run.Delivery
            .Setup(d => d.DispatchAsync(It.IsAny<Guid>(),
                It.Is<IReadOnlyList<AlertRuleChannelSnapshot>>(c => c.Single().AlertRuleId == failsDelivery),
                It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("delivery failed"));

        await run.Process().CheckAsync(
            Enabled(run.Rule(throwsOnCheck), run.Rule(Broken, id: failsDelivery), run.Rule(Broken, id: run.OtherRuleId)),
            CancellationToken.None);

        run.VerifySent(Times.Once(), run.OtherRuleId);
    }

    [Fact]
    public async Task A_notice_recorded_as_a_disable_lands_is_resolved_by_the_next_run_and_told_again_on_enable()
    {
        var run = await SeedAsync();
        await run.Process().CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);
        var notice = (await run.StandingAsync()).Single();
        await run.SetEnabledAsync(run.RuleId, false);

        await run.Process().CheckAsync(Enabled(), CancellationToken.None);

        run.VerifyResolved(notice, ExcursionCloseReason.RuleDisabled);
        (await run.StandingAsync()).Should().BeEmpty();

        await run.SetEnabledAsync(run.RuleId, true);
        await run.Process().CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);
        run.VerifySent(Times.Exactly(2), because: "a rule enabled again while still broken is told again");
    }

    [Fact]
    public async Task A_run_that_read_a_condition_the_rule_no_longer_has_records_nothing()
    {
        var run = await SeedAsync();
        var process = run.Process(repository: run.Counting(storeHoldsWhatTheRunRead: false));

        await process.CheckAsync(Enabled(run.Rule(OtherBroken)), CancellationToken.None);

        run.VerifySent(Times.Never(), because: "the store holds Broken; OtherBroken was edited away before the run recorded it");
        (await run.ExcursionsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_that_read_the_rule_before_it_was_disabled_records_nothing()
    {
        var run = await SeedAsync();
        var readBeforeTheDisable = run.Rule(Broken);
        await run.SetEnabledAsync(run.RuleId, false);

        await run.Process().CheckAsync(Enabled(readBeforeTheDisable), CancellationToken.None);

        run.VerifySent(Times.Never());
        (await run.ExcursionsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_notice_whose_retirement_failed_is_resolved_by_the_next_run()
    {
        var run = await SeedAsync();
        await run.Process().CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);
        var notice = (await run.StandingAsync()).Single();
        run.Resolution
            .Setup(h => h.HandleClosedAsync(It.IsAny<ExcursionTransition>(), run.Tenant, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("resolution failed"));
        await run.SetEnabledAsync(run.RuleId, false);
        var retire = () => run.Process().ResolveAsync(run.RuleId, ExcursionCloseReason.RuleDisabled, CancellationToken.None);
        await retire.Should().ThrowAsync<TimeoutException>();

        run.ResolutionResolvesInstances();
        await run.Process().CheckAsync(Enabled(), CancellationToken.None);

        (await run.StandingAsync()).Should().BeEmpty();
        run.Resolution.Verify(h => h.HandleClosedAsync(
            It.Is<ExcursionTransition>(t => t.ExcursionId == notice && t.CloseReason == ExcursionCloseReason.RuleDisabled),
            run.Tenant, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task A_superseded_notice_that_cannot_be_resolved_does_not_hold_back_the_new_one()
    {
        var run = await SeedAsync();
        await run.Process().CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);
        var first = (await run.StandingAsync()).Single();
        run.Resolution
            .Setup(h => h.HandleClosedAsync(It.Is<ExcursionTransition>(t => t.ExcursionId == first), run.Tenant, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("resolution failed"));

        await run.Process().CheckAsync(Enabled(run.Rule(OtherBroken)), CancellationToken.None);

        run.VerifySent(Times.Exactly(2));
    }

    [Fact]
    public async Task A_dispatched_notice_is_not_recorded_again_by_later_runs()
    {
        var run = await SeedAsync();
        var repository = run.Counting();
        var process = run.Process(repository: repository);

        await process.CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);
        await process.CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);
        await process.CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);

        repository.Records.Should().Be(1, "a run that finds the notice dispatched opens no write transaction");
        run.VerifySent(Times.Once());
    }

    [Fact]
    public async Task A_notice_of_a_rule_that_can_be_evaluated_again_is_resolved_by_a_later_run_when_the_first_try_fails()
    {
        var run = await SeedAsync();
        await run.Process().CheckAsync(Enabled(run.Rule(Broken)), CancellationToken.None);
        var notice = (await run.StandingAsync()).Single();
        var failing = true;
        run.Resolution
            .Setup(h => h.HandleClosedAsync(It.IsAny<ExcursionTransition>(), run.Tenant, It.IsAny<CancellationToken>()))
            .Returns((ExcursionTransition t, Guid _, CancellationToken ct) =>
                failing ? throw new TimeoutException("resolution failed") : run.ResolveInstancesAsync(t, ct));
        var log = new ConditionVersionLog();

        await run.Process(log).CheckAsync(Enabled(run.Rule(Valid)), CancellationToken.None);
        failing = false;
        await run.Process(log).CheckAsync(Enabled(run.Rule(Valid)), CancellationToken.None);

        (await run.StandingAsync()).Should().BeEmpty();
        notice.Should().NotBeEmpty();
    }
}
