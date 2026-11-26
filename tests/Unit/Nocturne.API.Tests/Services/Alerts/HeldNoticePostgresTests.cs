using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// The notice that a rule's hold reached the engine's bound (docs/alerts/engine-semantics.md §6.4)
/// is kept to one per hold by its row, on PostgreSQL under the runtime role and Row Level Security:
/// across writers on separate connections at once, as two instances of the API would be, and across
/// a restart, which finds it dispatched.
/// </summary>
[Trait("Category", "Integration")]
public class HeldNoticePostgresTests(HeldNoticePostgresTests.Database database)
    : IClassFixture<HeldNoticePostgresTests.Database>
{
    private static readonly DateTime HeldSince = new(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);
    private static DateTime Bound => HeldSince + RustAlertEngine.HoldBound();

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("held_notice");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private NocturneDbContext Context(Guid tenant) =>
        TestContexts.Context(database.AppConnectionString, tenant);

    /// <summary>A repository on connections of its own, as one instance of the API has.</summary>
    private AlertRepository Instance(Guid tenant) => new(new SuppliedContextFactory(() => Context(tenant)));

    /// <summary>
    /// A tenant with one enabled rule whose stored tracker state holds since
    /// <see cref="HeldSince"/>, its bound reported when <paramref name="reported"/>.
    /// </summary>
    private async Task<(Guid Tenant, Guid Rule)> SeedAsync(bool reported = true)
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'held-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"held-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var rule = Guid.CreateVersion7();
        await using var seed = Context(tenant);
        seed.AlertRules.Add(new AlertRuleEntity
        {
            Id = rule,
            TenantId = tenant,
            Name = "Urgent Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = """{"direction": "below", "value": 54}""",
            Severity = AlertRuleSeverity.Critical,
            IsEnabled = true,
            CreatedAt = HeldSince,
            UpdatedAt = HeldSince,
        });
        seed.AlertTrackerState.Add(new AlertTrackerStateEntity
        {
            AlertRuleId = rule,
            TenantId = tenant,
            State = "idle",
            UpdatedAt = reported ? Bound : HeldSince,
            HeldSince = HeldSince,
            HeldFacts = "glucose:stale",
        });
        await seed.SaveChangesAsync();
        return (tenant, rule);
    }

    private static AlertRuleSnapshot Snapshot(Guid tenant, Guid rule) =>
        new(rule, tenant, "Urgent Low", AlertConditionType.Threshold, """{"direction": "below", "value": 54}""",
            AlertRuleSeverity.Critical, "{}", 0, false, null);

    private sealed record Sent(Mock<IAlertDeliveryService> Delivery, HeldRuleNotice Notice);

    /// <summary>The notice service of one process, sending through a recorded delivery service.</summary>
    private Sent Process(
        Guid tenant, Guid rule, Mock<IAlertDeliveryService>? delivery = null, AlertRepository? repository = null)
    {
        delivery ??= new Mock<IAlertDeliveryService>();
        var rules = new Mock<IAlertRuleRepository>();
        rules.Setup(r => r.GetChannelsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                ids.ToDictionary(id => id, IReadOnlyList<AlertRuleChannelSnapshot> (_) => []));
        var enricher = new Mock<ISensorContextEnricher>();
        enricher.Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensorContext { LatestValue = null, LatestTimestamp = null, TrendRate = null, LastReadingAt = null });
        var tenantAccessor = Mock.Of<ITenantAccessor>(a => a.TenantId == tenant);
        return new Sent(delivery, new HeldRuleNotice(
            repository ?? Instance(tenant), rules.Object, tenantAccessor, delivery.Object, enricher.Object,
            Mock.Of<IExcursionResolutionHandler>(), TimeProvider.System, NullLogger<HeldRuleNotice>.Instance));
    }

    [Fact]
    public async Task Instances_recording_one_hold_at_once_record_one_notice()
    {
        var (tenant, rule) = await SeedAsync();

        var recorded = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Instance(tenant).RecordHeldNoticeAsync(rule, HeldSince, Bound, CancellationToken.None)));

        recorded.Select(r => r!.ExcursionId).Distinct().Should().ContainSingle();
        await using var db = Context(tenant);
        var row = await db.AlertExcursions.SingleAsync(e => e.AlertRuleId == rule);
        row.HeldSince.Should().Be(HeldSince);
        row.UnevaluableConditionVersion.Should().StartWith("held:",
            "an image that predates the hold column excludes the row as a notice");
    }

    [Fact]
    public async Task No_notice_is_recorded_for_a_disabled_rule_or_a_hold_that_is_over()
    {
        var (tenant, rule) = await SeedAsync();
        await using (var db = Context(tenant))
        {
            (await db.AlertRules.SingleAsync(r => r.Id == rule)).IsEnabled = false;
            await db.SaveChangesAsync();
        }
        (await Instance(tenant).RecordHeldNoticeAsync(rule, HeldSince, Bound, CancellationToken.None))
            .Should().BeNull("a disable that committed first wins");

        var (otherTenant, otherRule) = await SeedAsync();
        (await Instance(otherTenant).RecordHeldNoticeAsync(otherRule, HeldSince.AddMinutes(-5), Bound, CancellationToken.None))
            .Should().BeNull("the stored state no longer holds since that instant");
    }

    [Fact]
    public async Task A_held_notice_is_neither_an_alert_nor_a_notice_that_the_rule_cannot_be_evaluated()
    {
        var (tenant, rule) = await SeedAsync();
        var repository = Instance(tenant);

        var notice = (await repository.RecordHeldNoticeAsync(rule, HeldSince, Bound, CancellationToken.None))!;

        (await repository.CountActiveExcursionsAsync(CancellationToken.None)).Should().Be(0);
        (await repository.GetActiveAlertSnapshotsAsync(CancellationToken.None)).Should().BeEmpty();
        (await repository.GetStandingUnevaluableNoticesAsync(null, CancellationToken.None)).Should().BeEmpty();
        (await repository.GetStandingHeldNoticesAsync(rule, CancellationToken.None))
            .Should().Equal(new StandingHeldNotice(notice.ExcursionId, notice.InstanceId, rule, HeldSince, false));
        var snapshot = (await repository.GetExcursionRuleAsync(notice.ExcursionId, CancellationToken.None))!;
        (snapshot.IsHeldNotice, snapshot.IsUnevaluableNotice).Should().Be((true, false));
    }

    [Fact]
    public async Task A_reported_hold_is_sent_once_across_a_restart_and_resolved_when_the_hold_ends()
    {
        var (tenant, rule) = await SeedAsync();
        var delivery = new Mock<IAlertDeliveryService>();
        delivery.Setup(d => d.DispatchAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(), It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid instance, IReadOnlyList<AlertRuleChannelSnapshot> _, AlertPayload _, CancellationToken _) =>
            {
                await using var db = Context(tenant);
                db.AlertDeliveries.Add(new AlertDeliveryEntity
                {
                    Id = Guid.CreateVersion7(), TenantId = tenant, AlertInstanceId = instance,
                    ChannelType = ChannelType.Webhook, Destination = "hook", Payload = "{}",
                    Status = "delivered", CreatedAt = Bound,
                });
                await db.SaveChangesAsync();
            });

        await Process(tenant, rule, delivery).Notice.CheckAsync([Snapshot(tenant, rule)], CancellationToken.None);
        await Process(tenant, rule, delivery).Notice.CheckAsync([Snapshot(tenant, rule)], CancellationToken.None);

        delivery.Verify(d => d.DispatchAsync(
            It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
            It.Is<AlertPayload>(p => p.Kind == AlertPayloadKind.Held
                                     && p.GlucoseValue == null
                                     && p.HeldFacts!.Single() == new HeldFact("glucose", "stale")),
            It.IsAny<CancellationToken>()), Times.Once, "the second process finds the notice dispatched");

        await using (var db = Context(tenant))
        {
            var state = await db.AlertTrackerState.SingleAsync(s => s.AlertRuleId == rule);
            state.HeldSince = null;
            state.HeldFacts = null;
            await db.SaveChangesAsync();
        }
        await Process(tenant, rule).Notice.CheckAsync([Snapshot(tenant, rule)], CancellationToken.None);
        await using var read = Context(tenant);
        (await read.AlertExcursions.SingleAsync(e => e.AlertRuleId == rule)).EndedAt
            .Should().BeBefore(AlertExcursionEntity.NoticeStandingEnd, "a known root ended the hold the notice was for");
    }

    [Fact]
    public async Task A_hold_not_yet_reported_and_a_disabled_rule_are_sent_nothing()
    {
        var (tenant, rule) = await SeedAsync(reported: false);
        var notYet = Process(tenant, rule);
        await notYet.Notice.CheckAsync([Snapshot(tenant, rule)], CancellationToken.None);

        var (offTenant, offRule) = await SeedAsync();
        var disabled = Process(offTenant, offRule);
        await disabled.Notice.CheckAsync([], CancellationToken.None);

        foreach (var sent in new[] { notYet, disabled })
            sent.Delivery.VerifyNoOtherCalls();
        await using var db = Context(offTenant);
        (await db.AlertExcursions.CountAsync(e => e.AlertRuleId == offRule)).Should().Be(0);
    }

    [Fact]
    public async Task A_hold_that_reached_its_bound_unreported_is_due_and_one_reported_is_not()
    {
        var (tenant, rule) = await SeedAsync(reported: false);
        var (reportedTenant, reportedRule) = await SeedAsync();

        (await Process(tenant, rule).Notice.DueAsync([Snapshot(tenant, rule)], Bound, CancellationToken.None))
            .Select(r => r.Id).Should().Equal(rule);
        (await Process(tenant, rule).Notice.DueAsync([Snapshot(tenant, rule)], Bound.AddTicks(-10), CancellationToken.None))
            .Should().BeEmpty("the bound is not reached a microsecond early");
        (await Process(reportedTenant, reportedRule).Notice.DueAsync(
                [Snapshot(reportedTenant, reportedRule)], Bound.AddHours(1), CancellationToken.None))
            .Should().BeEmpty();
    }

    /// <summary>A repository whose record of one rule's notice fails, as a broken write does.</summary>
    private sealed class FailingFor(Guid failing, Func<NocturneDbContext> create) : AlertRepository(new SuppliedContextFactory(create))
    {
        public override Task<NoticeRecorded?> RecordHeldNoticeAsync(
            Guid ruleId, DateTime heldSince, DateTime at, CancellationToken ct) =>
            ruleId == failing
                ? throw new DbUpdateException("the write failed")
                : base.RecordHeldNoticeAsync(ruleId, heldSince, at, ct);
    }

    [Fact]
    public async Task A_notice_that_fails_for_one_rule_does_not_stop_another()
    {
        var (tenant, failing) = await SeedAsync();
        var other = Guid.CreateVersion7();
        await using (var db = Context(tenant))
        {
            db.AlertRules.Add(new AlertRuleEntity
            {
                Id = other, TenantId = tenant, Name = "Urgent Low", ConditionType = AlertConditionType.Threshold,
                ConditionParams = """{"direction": "below", "value": 54}""", Severity = AlertRuleSeverity.Critical,
                IsEnabled = true, CreatedAt = HeldSince, UpdatedAt = HeldSince,
            });
            db.AlertTrackerState.Add(new AlertTrackerStateEntity
            {
                AlertRuleId = other, TenantId = tenant, State = "idle", UpdatedAt = Bound,
                HeldSince = HeldSince, HeldFacts = "glucose:absent",
            });
            await db.SaveChangesAsync();
        }

        var sent = Process(tenant, failing, repository: new FailingFor(failing, () => Context(tenant)));
        await sent.Notice.CheckAsync([Snapshot(tenant, failing), Snapshot(tenant, other)], CancellationToken.None);

        sent.Delivery.Verify(d => d.DispatchAsync(
            It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
            It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()), Times.Once);
        (await Instance(tenant).GetStandingHeldNoticesAsync(other, CancellationToken.None)).Should().ContainSingle();
        (await Instance(tenant).GetStandingHeldNoticesAsync(failing, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_reported_hold_an_older_engine_wrote_over_is_not_sent()
    {
        var (tenant, rule) = await SeedAsync();
        await using (var db = Context(tenant))
        {
            var state = await db.AlertTrackerState.SingleAsync(s => s.AlertRuleId == rule);
            state.State = "active";
            state.UpdatedAt = Bound.AddDays(1);
            await db.SaveChangesAsync();
        }

        var sent = Process(tenant, rule);
        await sent.Notice.CheckAsync([Snapshot(tenant, rule)], CancellationToken.None);

        sent.Delivery.VerifyNoOtherCalls();
        (await Instance(tenant).GetStandingHeldNoticesAsync(rule, CancellationToken.None)).Should().BeEmpty(
            "an active state past the bound is one this engine never leaves while it holds");
    }
}
