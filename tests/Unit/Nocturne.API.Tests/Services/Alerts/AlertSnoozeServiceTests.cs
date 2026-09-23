using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
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
    private readonly Mock<IAlertDeliveryService> _delivery = new();
    private readonly Mock<ISensorContextEnricher> _enricher = new();
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly Mock<ICanonicalGlucoseService> _canonical = new();

    public AlertSnoozeServiceTests()
    {
        _enricher
            .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SensorContext ctx, IEnumerable<AlertRuleSnapshot> _, Guid _, CancellationToken _) => ctx);
        LatestReading(minutesAgo: 2, mgdl: 61, trendRate: -0.4);
        _repository
            .Setup(r => r.GetChannelsForRuleAsync(Tenant, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _repository
            .Setup(r => r.GetTenantAlertContextAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantAlertContext(Tenant, "Synthetic", "synthetic", "Synthetic", true, null));
    }

    private void LatestReading(double minutesAgo, double mgdl, double? trendRate = null) =>
        _canonical
            .Setup(c => c.GetLatestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensorGlucose { Timestamp = Now.AddMinutes(-minutesAgo), Mgdl = mgdl, TrendRate = trendRate });

    private AlertSnoozeService CreateService() => new(
        _factory,
        Mock.Of<ITenantAccessor>(a => a.TenantId == Tenant),
        _repository.Object,
        _delivery.Object,
        _enricher.Object,
        _canonical.Object,
        _broadcast.Object,
        _time,
        NullLogger<AlertSnoozeService>.Instance);

    private sealed record Seeded(Guid RuleId, Guid ExcursionId, Guid InstanceId);

    private async Task<Seeded> SeedAsync(
        string clientConfiguration = "{}",
        AlertRuleSeverity severity = AlertRuleSeverity.Warning,
        bool ruleEnabled = true,
        DateTime? snoozedUntil = null,
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
            SnoozedUntil = snoozedUntil,
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
    }

    [Fact]
    public async Task Snooze_withdraws_deliveries_still_waiting_to_be_sent_and_broadcasts()
    {
        var seeded = await SeedAsync();

        await CreateService().SnoozeAsync(seeded.InstanceId, 15, CancellationToken.None);

        _repository.Verify(r => r.ExpirePendingDeliveriesAsync(
            Tenant, It.Is<IReadOnlyList<Guid>>(ids => ids.SequenceEqual(new[] { seeded.InstanceId })),
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
    [Fact]
    public async Task Snoozed_alert_still_reads_as_firing_and_unacknowledged_to_escalation_rules()
    {
        var seeded = await SeedAsync();
        await CreateService().SnoozeAsync(seeded.InstanceId, 30, CancellationToken.None);

        var activeAlerts = await new AlertRepository(_factory).GetActiveAlertSnapshotsAsync(Tenant, CancellationToken.None);
        var context = new SensorContext
        {
            LatestValue = 61,
            LatestTimestamp = Now,
            TrendRate = null,
            LastReadingAt = Now,
            ActiveAlerts = activeAlerts,
        };

        var unacknowledged = await new AlertStateEvaluator(_time).EvaluateAsync(
            $"{{\"alert_id\":\"{seeded.RuleId}\",\"state\":\"unacknowledged\",\"for_minutes\":15}}",
            context, CancellationToken.None);

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

    [Fact]
    public async Task Resume_redispatches_a_lapsed_snooze_whose_alert_still_stands()
    {
        var seeded = await SeedAsync();

        var resumed = await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None);

        resumed.Should().BeTrue();
        _delivery.Verify(d => d.DispatchAsync(
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
    public async Task Resume_carries_no_reading_when_the_newest_one_is_stale()
    {
        var seeded = await SeedAsync();
        LatestReading(minutesAgo: 30, mgdl: 61);

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeTrue();

        _delivery.Verify(d => d.DispatchAsync(
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
    }

    [Fact]
    public async Task Resume_does_nothing_once_acknowledged()
    {
        var seeded = await SeedAsync(acknowledged: true);

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeFalse();
        _delivery.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Resume_does_nothing_when_the_condition_has_cleared_into_hysteresis()
    {
        var seeded = await SeedAsync(inHysteresis: true);

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeFalse();
        _delivery.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Resume_does_nothing_for_a_closed_excursion()
    {
        var seeded = await SeedAsync(ended: true);

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeFalse();
        _delivery.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Resume_does_nothing_for_a_disabled_rule()
    {
        var seeded = await SeedAsync(ruleEnabled: false);

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeFalse();
        _delivery.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Resume_respects_DND_for_a_non_critical_rule()
    {
        var seeded = await SeedAsync();
        _enricher
            .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SensorContext ctx, IEnumerable<AlertRuleSnapshot> _, Guid _, CancellationToken _) =>
                ctx with { ActiveDndScopes = new HashSet<DndScope> { DndScope.Lows } });

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeFalse();
        _delivery.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Resume_pierces_DND_for_a_critical_rule()
    {
        var seeded = await SeedAsync(severity: AlertRuleSeverity.Critical);
        _enricher
            .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SensorContext ctx, IEnumerable<AlertRuleSnapshot> _, Guid _, CancellationToken _) =>
                ctx with { ActiveDndScopes = new HashSet<DndScope> { DndScope.All } });

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Resume_still_delivers_when_the_DND_lookup_fails()
    {
        var seeded = await SeedAsync();
        _enricher
            .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), Tenant, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("synthetic"));

        (await CreateService().ResumeAsync(seeded.InstanceId, CancellationToken.None)).Should().BeTrue();
    }
}
