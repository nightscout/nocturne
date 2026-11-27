using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.API.Services.Realtime;
using Nocturne.Tests.Shared.Logging;
using Nocturne.API.Tests.Services.Alerts.Engines;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class AlertSnoozeServiceTests
{
    private static readonly Guid Tenant = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly DateTime Now = new(2026, 9, 23, 3, 0, 0, DateTimeKind.Utc);

    private sealed class SharedInMemoryFactory(string dbName) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext()
        {
            var ctx = TestDbContextFactory.CreateInMemoryContext(dbName);
            ctx.TenantId = Tenant;
            return ctx;
        }
    }

    private readonly IDbContextFactory<NocturneDbContext> _factory = new SharedInMemoryFactory($"snooze_{Guid.NewGuid()}");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Now));
    private readonly Mock<IAlertRepository> _repository = new();
    private readonly Mock<IAlertRuleRepository> _rules = new();
    private readonly Mock<IAlertDeliveryService> _delivery = new();
    private readonly Mock<ISensorContextEnricher> _enricher = new();
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly Mock<IEngineAlarmRelay> _alarmRelay = new();
    private readonly Mock<ICanonicalGlucoseService> _canonical = new();

    public AlertSnoozeServiceTests()
    {
        _enricher
            .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SensorContext ctx, IEnumerable<AlertRuleSnapshot> _, CancellationToken _) => ctx);
        LatestReading(minutesAgo: 2, mgdl: 61, trendRate: -0.4);
        _rules
            .Setup(r => r.GetChannelsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, IReadOnlyList<AlertRuleChannelSnapshot>>());
        _repository
            .Setup(r => r.GetTenantAlertContextAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantAlertContext(Tenant, "Synthetic", "synthetic", "Synthetic", true, null));
        _repository
            .Setup(r => r.EndLapsedSnoozeAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, DateTime _, DateTime? renotifyAt, CancellationToken _) =>
                renotifyAt is null ? LapsedSnoozeEnd.Ended : LapsedSnoozeEnd.EndedHoldingOpening);
    }

    private static readonly DateTime Lapsed = Now.AddMinutes(-1);

    private void LatestReading(double minutesAgo, double mgdl, double? trendRate = null) =>
        _canonical
            .Setup(c => c.GetLatestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensorGlucose { Timestamp = Now.AddMinutes(-minutesAgo), Mgdl = mgdl, TrendRate = trendRate });

    private AlertSnoozeService CreateService(ILogger<AlertSnoozeService>? logger = null) => new(
        _factory.AsNocturneFactory(),
        SettableTenantAccessor.Resolved(Tenant),
        _repository.Object,
        _rules.Object,
        _delivery.Object,
        _enricher.Object,
        _canonical.Object,
        _broadcast.Object,
        _alarmRelay.Object,
        Mock.Of<IExcursionResolutionHandler>(),
        _time,
        logger ?? NullLogger<AlertSnoozeService>.Instance);

    private sealed record Seeded(Guid RuleId, Guid ExcursionId, Guid InstanceId);

    private async Task<Seeded> SeedAsync(
        string clientConfiguration = "{}",
        AlertRuleSeverity severity = AlertRuleSeverity.Warning,
        bool ruleEnabled = true,
        DateTime? snoozedUntil = null,
        bool lapsed = false,
        int snoozeCount = 0,
        bool acknowledged = false,
        bool inHysteresis = false,
        bool ended = false)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var rule = new AlertRuleEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            Name = "Low",
            Severity = severity,
            IsEnabled = ruleEnabled,
            ClientConfiguration = clientConfiguration,
            ScopeClass = RuleScopeClass.Low,
        };
        var excursion = new AlertExcursionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            AlertRuleId = rule.Id,
            StartedAt = Now.AddMinutes(-30),
            AcknowledgedAt = acknowledged ? Now.AddMinutes(-5) : null,
            HysteresisStartedAt = inHysteresis ? Now.AddMinutes(-1) : null,
            EndedAt = ended ? Now.AddMinutes(-1) : null,
        };
        var instance = new AlertInstanceEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            AlertExcursionId = excursion.Id,
            Status = acknowledged ? "acknowledged" : ended ? "resolved" : "triggered",
            TriggeredAt = excursion.StartedAt,
            ResolvedAt = ended ? Now.AddMinutes(-1) : null,
            SnoozedUntil = snoozedUntil ?? (lapsed ? Lapsed : null),
            SnoozeCount = snoozeCount,
        };
        db.AlertRules.Add(rule);
        db.AlertExcursions.Add(excursion);
        db.AlertInstances.Add(instance);
        await db.SaveChangesAsync();
        return new Seeded(rule.Id, excursion.Id, instance.Id);
    }

    private async Task<AlertInstanceEntity> InstanceAsync(Guid id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.AlertInstances.AsNoTracking().SingleAsync(i => i.Id == id);
    }

    private async Task<AlertExcursionEntity> ExcursionAsync(Guid id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.AlertExcursions.AsNoTracking().SingleAsync(e => e.Id == id);
    }

    // ---- SnoozeAsync ----

    [Fact]
    public async Task Snooze_sets_the_window_from_now_and_counts_it()
    {
        var seeded = await SeedAsync();

        var outcome = await CreateService().SnoozeAsync(seeded.InstanceId, 15, CancellationToken.None);

        outcome.Should().Be(new SnoozeOutcome(SnoozeResult.Snoozed, Now.AddMinutes(15)));
        var instance = await InstanceAsync(seeded.InstanceId);
        instance.SnoozedUntil.Should().Be(Now.AddMinutes(15));
        instance.SnoozeCount.Should().Be(1);
        _alarmRelay.Verify(
            r => r.ClearIfQuietAsync(
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { seeded.ExcursionId })),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Snooze_withdraws_deliveries_still_waiting_to_be_sent_and_broadcasts()
    {
        var seeded = await SeedAsync();

        await CreateService().SnoozeAsync(seeded.InstanceId, 15, CancellationToken.None);

        _repository.Verify(r => r.ExpirePendingDeliveriesAsync(
            It.Is<IReadOnlyList<Guid>>(ids => ids.SequenceEqual(new[] { seeded.InstanceId })),
            It.IsAny<CancellationToken>()), Times.Once);
        _broadcast.Verify(b => b.BroadcastAlertEventAsync("alert_snoozed", It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task Snooze_is_not_an_acknowledgement()
    {
        var seeded = await SeedAsync();

        await CreateService().SnoozeAsync(seeded.InstanceId, 15, CancellationToken.None);

        (await ExcursionAsync(seeded.ExcursionId)).AcknowledgedAt.Should().BeNull();
        (await InstanceAsync(seeded.InstanceId)).Status.Should().Be("triggered");
    }

    /// <summary>
    /// Escalation is a separate rule over <c>alert_state</c>; a snooze silences only the snoozed
    /// instance, so an escalation keyed on "unacknowledged" still sees the parent as such.
    /// </summary>
    [NativeFact]
    public async Task Snoozed_alert_still_reads_as_firing_and_unacknowledged_to_escalation_rules()
    {
        var seeded = await SeedAsync();
        await CreateService().SnoozeAsync(seeded.InstanceId, 30, CancellationToken.None);

        var activeAlerts = await new AlertRepository(_factory.AsNocturneFactory()).GetActiveAlertSnapshotsAsync(CancellationToken.None);
        var context = new SensorContext
        {
            LatestValue = 61,
            LatestTimestamp = Now,
            TrendRate = null,
            LastReadingAt = Now,
            ActiveAlerts = activeAlerts,
        };
        var time = new ManualTimeProvider();
        time.SetUtcNow(Now);
        var escalation =
            $$$"""[{"type":"alert_state","alert_state":{"alert_id":"{{{seeded.RuleId}}}","state":"unacknowledged","for_minutes":15}}]""";
        var watcher = new AlertRule
        {
            Id = Guid.NewGuid(),
            ClientConfiguration = $$$"""{"snooze":{"smartSnooze":true,"conditions":{{{escalation}}}}}""",
        };
        var engine = EngineTestHarness.BuildRustEngine(time, new InMemoryTrackerRepository([watcher]));

        var unacknowledged = await engine.EvaluateSnoozeAsync(
            watcher.Id, JsonDocument.Parse(escalation).RootElement, context, CancellationToken.None);

        unacknowledged.Should().BeTrue();
    }

    [Fact]
    public async Task Snoozing_again_replaces_the_window_rather_than_adding_to_it()
    {
        var seeded = await SeedAsync(snoozedUntil: Now.AddMinutes(50), snoozeCount: 1);

        await CreateService().SnoozeAsync(seeded.InstanceId, 5, CancellationToken.None);

        var instance = await InstanceAsync(seeded.InstanceId);
        instance.SnoozedUntil.Should().Be(Now.AddMinutes(5));
        instance.SnoozeCount.Should().Be(2);
    }

    [Fact]
    public async Task Snooze_of_unknown_instance_is_not_found()
    {
        var outcome = await CreateService().SnoozeAsync(Guid.NewGuid(), 15, CancellationToken.None);

        outcome.Result.Should().Be(SnoozeResult.NotFound);
    }

    [Fact]
    public async Task Snooze_of_resolved_instance_is_refused_without_spending_the_count()
    {
        var seeded = await SeedAsync(ended: true);

        var outcome = await CreateService().SnoozeAsync(seeded.InstanceId, 15, CancellationToken.None);

        outcome.Result.Should().Be(SnoozeResult.NotActive);
        var instance = await InstanceAsync(seeded.InstanceId);
        instance.SnoozeCount.Should().Be(0);
        instance.SnoozedUntil.Should().BeNull();
    }

    [Fact]
    public async Task Snooze_stops_at_the_shared_default_max_count()
    {
        var seeded = await SeedAsync(snoozeCount: SmartSnoozeConfig.DefaultMaxCount);

        var outcome = await CreateService().SnoozeAsync(seeded.InstanceId, 15, CancellationToken.None);

        outcome.Result.Should().Be(SnoozeResult.LimitReached);
        (await InstanceAsync(seeded.InstanceId)).SnoozedUntil.Should().BeNull();
        _alarmRelay.Verify(
            r => r.ClearIfQuietAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(1, 0, SnoozeResult.Snoozed)]
    [InlineData(1, 1, SnoozeResult.LimitReached)]
    [InlineData(8, 7, SnoozeResult.Snoozed)]
    public async Task Snooze_honours_the_rules_max_count(int maxCount, int used, SnoozeResult expected)
    {
        var seeded = await SeedAsync(
            clientConfiguration: $"{{\"snooze\":{{\"maxCount\":{maxCount}}}}}", snoozeCount: used);

        var outcome = await CreateService().SnoozeAsync(seeded.InstanceId, 15, CancellationToken.None);

        outcome.Result.Should().Be(expected);
    }

    // ---- ResumeAsync ----

    private void ShouldEndTheSnoozeWithoutASend(Seeded seeded) =>
        _repository.Verify(r => r.EndLapsedSnoozeAsync(
            seeded.InstanceId, Lapsed, null, It.IsAny<CancellationToken>()), Times.Once);

    [Fact]
    public async Task Resume_redispatches_a_lapsed_snooze_whose_alert_still_stands()
    {
        var seeded = await SeedAsync(lapsed: true);

        var resumed = await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None);

        resumed.Should().BeTrue();
        _delivery.Verify(d => d.RenotifyAsync(
            seeded.InstanceId,
            It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
            It.Is<AlertPayload>(p =>
                p.InstanceId == seeded.InstanceId
                && p.ExcursionId == seeded.ExcursionId
                && p.GlucoseValue == 61m
                && p.RuleName == "Low"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Resume_ends_the_lapsed_snooze_in_the_write_that_starts_the_re_notification_before_it_sends()
    {
        var seeded = await SeedAsync(lapsed: true);
        var order = new List<string>();
        _repository
            .Setup(r => r.EndLapsedSnoozeAsync(seeded.InstanceId, Lapsed, Now, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("ended"))
            .ReturnsAsync(LapsedSnoozeEnd.EndedHoldingOpening);
        _delivery
            .Setup(d => d.RenotifyAsync(seeded.InstanceId, It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(), It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("sent"))
            .Returns(Task.CompletedTask);

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeTrue();

        order.Should().Equal("ended", "sent");
        _repository.Verify(r => r.SettleTransitionAsync(
            seeded.ExcursionId, ExcursionTransitionType.ExcursionOpened, Now, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Resume_leaves_an_opening_another_run_holds_owed_unsettled()
    {
        var seeded = await SeedAsync(lapsed: true);
        _repository
            .Setup(r => r.EndLapsedSnoozeAsync(seeded.InstanceId, Lapsed, Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(LapsedSnoozeEnd.Ended);

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeTrue();

        _repository.Verify(r => r.SettleTransitionAsync(
            It.IsAny<Guid>(), It.IsAny<ExcursionTransitionType>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Resume_sends_nothing_when_another_run_ended_the_snooze_first()
    {
        var seeded = await SeedAsync(lapsed: true);
        _repository
            .Setup(r => r.EndLapsedSnoozeAsync(seeded.InstanceId, Lapsed, Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(LapsedSnoozeEnd.Lost);

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeFalse();

        _delivery.VerifyNoOtherCalls();
        _repository.Verify(r => r.SettleTransitionAsync(
            It.IsAny<Guid>(), It.IsAny<ExcursionTransitionType>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Resume_does_nothing_for_an_instance_with_no_snooze()
    {
        var seeded = await SeedAsync();

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeFalse();

        _delivery.VerifyNoOtherCalls();
        _repository.Verify(r => r.EndLapsedSnoozeAsync(
            It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Resume_carries_the_reading_an_alert_selects_not_one_dated_ahead()
    {
        var seeded = await SeedAsync(lapsed: true);
        _canonical
            .Setup(c => c.GetLatestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensorGlucose { Timestamp = Now.AddHours(1), Mgdl = 300 });
        _canonical
            .Setup(c => c.GetLatestAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensorGlucose { Timestamp = Now.AddMinutes(-2), Mgdl = 61 });

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeTrue();

        _delivery.Verify(d => d.RenotifyAsync(
            seeded.InstanceId,
            It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
            It.Is<AlertPayload>(p => p.GlucoseValue == 61m),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Resume_carries_the_rate_derived_from_the_previous_reading()
    {
        var seeded = await SeedAsync(lapsed: true);
        LatestReading(minutesAgo: 2, mgdl: 61);
        _canonical
            .Setup(c => c.GetStreamPredecessorAsync(
                It.IsAny<SensorGlucose>(), Now.AddMinutes(-2) - ReadingTrend.MaxGap, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensorGlucose { Id = Guid.NewGuid(), Timestamp = Now.AddMinutes(-7), Mgdl = 71 });

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeTrue();

        _delivery.Verify(d => d.RenotifyAsync(
            seeded.InstanceId,
            It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
            It.Is<AlertPayload>(p => p.TrendRate == -2m),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The re-notification's context names its fresh reading, as the sweep's snooze context does, so a
    /// windowed rule reads that reading's recent readings; a stale reading is named by neither.
    /// </summary>
    [Theory]
    [InlineData(2, true)]
    [InlineData(30, false)]
    public async Task Resume_names_the_fresh_reading_its_recent_readings_are_read_from(double minutesAgo, bool named)
    {
        var seeded = await SeedAsync(lapsed: true);
        var reading = new SensorGlucose
        {
            Id = Guid.NewGuid(), Timestamp = Now.AddMinutes(-minutesAgo), Mgdl = 61,
            DataSource = "xdrip", Device = "g7", PatientDeviceId = Guid.NewGuid(),
        };
        _canonical.Setup(c => c.GetLatestAsync(It.IsAny<CancellationToken>())).ReturnsAsync(reading);
        var contexts = new List<SensorContext>();
        _enricher
            .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<CancellationToken>()))
            .Callback<SensorContext, IEnumerable<AlertRuleSnapshot>, CancellationToken>((ctx, _, _) => contexts.Add(ctx))
            .ReturnsAsync((SensorContext ctx, IEnumerable<AlertRuleSnapshot> _, CancellationToken _) => ctx);

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeTrue();

        contexts.Should().ContainSingle().Which.LatestSource.Should().Be(named ? ReadingSource.Of(reading) : null);
    }

    [Fact]
    public async Task Resume_carries_no_reading_when_the_newest_one_is_stale()
    {
        var seeded = await SeedAsync(lapsed: true);
        LatestReading(minutesAgo: 30, mgdl: 61);

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeTrue();

        _delivery.Verify(d => d.RenotifyAsync(
            seeded.InstanceId,
            It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
            It.Is<AlertPayload>(p => p.GlucoseValue == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Resume_does_nothing_while_the_snooze_is_still_in_force()
    {
        var seeded = await SeedAsync(snoozedUntil: Now.AddMinutes(1), snoozeCount: 1);

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeFalse();
        _delivery.VerifyNoOtherCalls();
        _repository.Verify(r => r.EndLapsedSnoozeAsync(
            It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Resume_does_nothing_once_acknowledged()
    {
        var seeded = await SeedAsync(lapsed: true, acknowledged: true);

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeFalse();
        _delivery.VerifyNoOtherCalls();
        ShouldEndTheSnoozeWithoutASend(seeded);
    }

    [Fact]
    public async Task Resume_does_nothing_when_the_condition_has_cleared_into_hysteresis()
    {
        var seeded = await SeedAsync(lapsed: true, inHysteresis: true);

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeFalse();
        _delivery.VerifyNoOtherCalls();
        ShouldEndTheSnoozeWithoutASend(seeded);
    }

    [Fact]
    public async Task Resume_does_nothing_for_a_closed_excursion()
    {
        var seeded = await SeedAsync(lapsed: true, ended: true);

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeFalse();
        _delivery.VerifyNoOtherCalls();
        ShouldEndTheSnoozeWithoutASend(seeded);
    }

    [Fact]
    public async Task Resume_does_nothing_for_a_disabled_rule()
    {
        var seeded = await SeedAsync(lapsed: true, ruleEnabled: false);

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeFalse();
        _delivery.VerifyNoOtherCalls();
        ShouldEndTheSnoozeWithoutASend(seeded);
    }

    [Theory]
    [InlineData(nameof(AlertRuleEntity.ConditionType))]
    [InlineData(nameof(AlertRuleEntity.Severity))]
    [InlineData(nameof(AlertRuleEntity.ScopeClass))]
    public async Task Resume_does_nothing_for_a_rule_holding_a_value_no_member_has(string column)
    {
        var seeded = await SeedAsync(lapsed: true, severity: AlertRuleSeverity.Critical);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var rule = await db.AlertRules.SingleAsync(r => r.Id == seeded.RuleId);
            switch (column)
            {
                case nameof(AlertRuleEntity.ConditionType): rule.ConditionType = (AlertConditionType)(-1); break;
                case nameof(AlertRuleEntity.Severity): rule.Severity = (AlertRuleSeverity)(-1); break;
                default: rule.ScopeClass = (RuleScopeClass)(-1); break;
            }
            await db.SaveChangesAsync();
        }

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None))
            .Should().BeFalse("the rule is not evaluated, so nothing says its alert still stands");
        _delivery.VerifyNoOtherCalls();
        ShouldEndTheSnoozeWithoutASend(seeded);
    }

    [Fact]
    public async Task Resume_respects_DND_for_a_non_critical_rule()
    {
        var seeded = await SeedAsync(lapsed: true);
        _enricher
            .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SensorContext ctx, IEnumerable<AlertRuleSnapshot> _, CancellationToken _) =>
                ctx with { ActiveDndScopes = new HashSet<DndScope> { DndScope.Lows } });

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeFalse();
        _delivery.VerifyNoOtherCalls();
        ShouldEndTheSnoozeWithoutASend(seeded);
    }

    [Fact]
    public async Task Resume_pierces_DND_for_a_critical_rule()
    {
        var seeded = await SeedAsync(lapsed: true, severity: AlertRuleSeverity.Critical);
        _enricher
            .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SensorContext ctx, IEnumerable<AlertRuleSnapshot> _, CancellationToken _) =>
                ctx with { ActiveDndScopes = new HashSet<DndScope> { DndScope.All } });

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Resume_still_delivers_when_the_DND_lookup_cannot_read_the_store()
    {
        var seeded = await SeedAsync(lapsed: true);
        var failure = new NpgsqlException("connection refused");
        _enricher
            .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        var logger = new ListLogger<AlertSnoozeService>();

        (await CreateService(logger).ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeTrue();

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Exception == failure);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(TimeoutException))]
    public async Task Resume_lets_a_defect_in_the_DND_lookup_reach_the_caller(Type defect)
    {
        var seeded = await SeedAsync(lapsed: true);
        _enricher
            .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync((Exception)Activator.CreateInstance(defect)!);

        var resume = () => CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None);

        await resume.Should().ThrowAsync<Exception>().Where(e => e.GetType() == defect);
        _delivery.VerifyNoOtherCalls();
    }
}
