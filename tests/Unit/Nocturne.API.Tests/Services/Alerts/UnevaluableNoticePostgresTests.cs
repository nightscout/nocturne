using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Reports;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// The row that records a notice that a rule cannot be evaluated is what keeps it to one per
/// version of the condition, so it holds on PostgreSQL under the runtime role and Row Level
/// Security, for writers on separate connections at once, as two instances of the API would be.
/// </summary>
[Trait("Category", "Integration")]
public class UnevaluableNoticePostgresTests(UnevaluableNoticePostgresTests.Database database)
    : IClassFixture<UnevaluableNoticePostgresTests.Database>
{
    private const string Version = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string OtherVersion = "0000000000000000000000000000000000000000000000000000000000000002";
    private static readonly DateTime At = new(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("unevaluable_notice");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private NocturneDbContext Context(Guid tenant) =>
        TestContexts.Context(database.AppConnectionString, tenant);

    /// <summary>A repository on connections of its own, as one instance of the API has.</summary>
    private AlertRepository Instance(Guid tenant) => new(new SuppliedContextFactory(() => Context(tenant)));

    /// <summary>A repository read by a member whose history is clamped to the last 24 hours.</summary>
    private AlertRepository ClampedMember(Guid tenant) => new(new SuppliedContextFactory(() =>
    {
        var reader = new CategoryReadContext();
        reader.ClampMemberHistory();
        return Context(tenant).ReadingFrom(reader: reader);
    }));

    private async Task<(Guid Tenant, Guid Rule)> SeedAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'notice-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"notice-{tenant:N}"));
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
            ConditionParams = """{"threshold": 54}""",
            Severity = AlertRuleSeverity.Critical,
            IsEnabled = true,
            CreatedAt = At,
            UpdatedAt = At,
        });
        await seed.SaveChangesAsync();
        return (tenant, rule);
    }

    /// <summary>The rule <see cref="SeedAsync"/> stores, as a run reads it.</summary>
    private static AlertRuleSnapshot Snapshot(Guid tenant, Guid rule) =>
        new(rule, tenant, "Urgent Low", AlertConditionType.Threshold, """{"threshold": 54}""",
            AlertRuleSeverity.Critical, "{}", 0, false, null);

    [Fact]
    public async Task No_notice_is_recorded_for_a_condition_the_rule_no_longer_has()
    {
        var (tenant, rule) = await SeedAsync();
        await using (var db = Context(tenant))
        {
            (await db.AlertRules.SingleAsync(r => r.Id == rule)).ConditionParams = """{"threshold": 55}""";
            await db.SaveChangesAsync();
        }

        (await Instance(tenant).RecordUnevaluableNoticeAsync(Snapshot(tenant, rule), Version, At, CancellationToken.None))
            .Should().BeNull("an edit that committed after the run read the rule wins");
        await using var read = Context(tenant);
        (await read.AlertExcursions.CountAsync(e => e.AlertRuleId == rule)).Should().Be(0);
    }

    [Fact]
    public async Task Instances_recording_one_version_at_once_record_one_notice()
    {
        var (tenant, rule) = await SeedAsync();

        var recorded = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Instance(tenant).RecordUnevaluableNoticeAsync(Snapshot(tenant, rule), Version, At, CancellationToken.None)));

        recorded.Select(r => r!.ExcursionId).Distinct().Should().ContainSingle();
        (await Instance(tenant).RecordUnevaluableNoticeAsync(Snapshot(tenant, rule), Version, At, CancellationToken.None))!.ExcursionId
            .Should().Be(recorded[0]!.ExcursionId, "a restarted instance finds the notice standing");
        await using var db = Context(tenant);
        (await db.AlertExcursions.CountAsync(e => e.AlertRuleId == rule)).Should().Be(1);
    }

    [Fact]
    public async Task Each_version_stands_until_resolved_and_a_resolved_one_no_longer_stands()
    {
        var (tenant, rule) = await SeedAsync();
        var repository = Instance(tenant);

        var first = (await repository.RecordUnevaluableNoticeAsync(Snapshot(tenant, rule), Version, At, CancellationToken.None))!;
        var second = (await repository.RecordUnevaluableNoticeAsync(Snapshot(tenant, rule), OtherVersion, At, CancellationToken.None))!;

        (await repository.GetStandingUnevaluableNoticesAsync(null, CancellationToken.None))
            .Should().BeEquivalentTo([
                new StandingUnevaluableNotice(first.ExcursionId, first.InstanceId, rule, Version, false),
                new StandingUnevaluableNotice(second.ExcursionId, second.InstanceId, rule, OtherVersion, false),
            ]);
        await repository.ResolveInstancesForExcursionAsync(first.ExcursionId, At, "manual", CancellationToken.None);
        (await repository.GetStandingUnevaluableNoticesAsync(rule, CancellationToken.None))
            .Select(n => n.ExcursionId).Should().Equal(second.ExcursionId);

        await repository.ResolveInstancesForExcursionAsync(second.ExcursionId, At, "manual", CancellationToken.None);
        (await repository.GetStandingUnevaluableNoticesAsync(rule, CancellationToken.None)).Should().BeEmpty();
        var again = (await repository.RecordUnevaluableNoticeAsync(Snapshot(tenant, rule), Version, At, CancellationToken.None))!;
        again.ExcursionId.Should().NotBe(first.ExcursionId, "a version whose notice was resolved, stored again, is told again");
        again.Dispatched.Should().BeFalse();
    }

    [Fact]
    public async Task No_notice_is_recorded_for_a_rule_that_is_not_enabled()
    {
        var (tenant, rule) = await SeedAsync();
        await using (var db = Context(tenant))
        {
            (await db.AlertRules.SingleAsync(r => r.Id == rule)).IsEnabled = false;
            await db.SaveChangesAsync();
        }

        (await Instance(tenant).RecordUnevaluableNoticeAsync(Snapshot(tenant, rule), Version, At, CancellationToken.None))
            .Should().BeNull("a disable that committed first wins over a sweep that read the rule enabled");
        await using var read = Context(tenant);
        (await read.AlertExcursions.CountAsync(e => e.AlertRuleId == rule)).Should().Be(0);
    }

    [Fact]
    public async Task A_member_whose_history_is_clamped_sees_a_notice_that_stands_from_days_ago()
    {
        var (tenant, rule) = await SeedAsync();
        var daysAgo = DateTime.UtcNow.AddDays(-3);
        var notice = (await Instance(tenant).RecordUnevaluableNoticeAsync(Snapshot(tenant, rule), Version, daysAgo, CancellationToken.None))!;

        var clamped = ClampedMember(tenant);

        (await clamped.GetStandingUnevaluableNoticesAsync(rule, CancellationToken.None))
            .Select(n => n.ExcursionId).Should().Equal(notice.ExcursionId);
        (await clamped.RecordUnevaluableNoticeAsync(Snapshot(tenant, rule), Version, DateTime.UtcNow, CancellationToken.None))!
            .ExcursionId.Should().Be(notice.ExcursionId, "the clamped reader finds the standing notice rather than recording another");

        await clamped.EndNoticeAsync(notice.ExcursionId, DateTime.UtcNow, CancellationToken.None);
        await clamped.ResolveInstancesForExcursionAsync(notice.ExcursionId, DateTime.UtcNow, "rule-disabled", CancellationToken.None);
        (await Instance(tenant).GetStandingUnevaluableNoticesAsync(rule, CancellationToken.None)).Should().BeEmpty(
            "a clamped member who disables the rule resolves its notice");
    }

    [Fact]
    public async Task A_notice_counts_as_dispatched_once_its_dispatch_is_settled_with_a_delivery_row_or_a_silence()
    {
        var (tenant, rule) = await SeedAsync();
        var repository = Instance(tenant);
        var notice = (await repository.RecordUnevaluableNoticeAsync(Snapshot(tenant, rule), Version, At, CancellationToken.None))!;
        notice.Dispatched.Should().BeFalse();
        var handedBack = (await repository.RecordUnevaluableNoticeAsync(Snapshot(tenant, rule), Version, At, CancellationToken.None))!;
        (handedBack.ExcursionId, handedBack.InstanceId, handedBack.Dispatched)
            .Should().Be((notice.ExcursionId, notice.InstanceId, false), "a notice whose dispatch never finished is handed back");

        await using (var db = Context(tenant))
        {
            db.AlertDeliveries.Add(new AlertDeliveryEntity
            {
                Id = Guid.CreateVersion7(), TenantId = tenant, AlertInstanceId = notice.InstanceId,
                ChannelType = ChannelType.HomeAssistant, Destination = "kitchen-ha", Payload = "{}",
                Status = "delivered", CreatedAt = At,
            });
            await db.SaveChangesAsync();
        }
        (await repository.RecordUnevaluableNoticeAsync(Snapshot(tenant, rule), Version, At, CancellationToken.None))!.Dispatched
            .Should().BeFalse("a dispatch that died after its first row may still owe another channel");
        await repository.SettleTransitionAsync(notice.ExcursionId, ExcursionTransitionType.ExcursionOpened, CancellationToken.None);
        (await repository.RecordUnevaluableNoticeAsync(Snapshot(tenant, rule), Version, At, CancellationToken.None))!.Dispatched.Should().BeTrue();
        (await repository.GetHomeAssistantInstancesSentExcursionAsync(notice.ExcursionId, CancellationToken.None))
            .Should().Equal("kitchen-ha");

        var silenced = (await repository.RecordUnevaluableNoticeAsync(Snapshot(tenant, rule), OtherVersion, At, CancellationToken.None))!;
        await repository.MarkInstanceSuppressedAsync(silenced.InstanceId, "dnd:all", CancellationToken.None);
        await repository.SettleTransitionAsync(silenced.ExcursionId, ExcursionTransitionType.ExcursionOpened, CancellationToken.None);
        (await repository.RecordUnevaluableNoticeAsync(Snapshot(tenant, rule), OtherVersion, At, CancellationToken.None))!.Dispatched.Should().BeTrue();

        await using (var db = Context(tenant))
        {
            var written = Guid.CreateVersion7();
            db.AlertExcursions.Add(new AlertExcursionEntity
            {
                Id = written, TenantId = tenant, AlertRuleId = rule, StartedAt = At,
                EndedAt = AlertExcursionEntity.NoticeStandingEnd, UnevaluableConditionVersion = "0000000000000000000000000000000000000000000000000000000000000003",
            });
            db.AlertInstances.Add(new AlertInstanceEntity
            {
                Id = Guid.CreateVersion7(), TenantId = tenant, AlertExcursionId = written, Status = "triggered", TriggeredAt = At,
            });
            await db.SaveChangesAsync();
        }
        (await repository.GetStandingUnevaluableNoticesAsync(rule, CancellationToken.None))
            .Single(n => n.ConditionVersion.EndsWith('3')).Dispatched
            .Should().BeFalse("a notice recorded before its dispatch was owed, and never sent, is still sent");
    }

    [Fact]
    public async Task Neither_report_probe_finds_an_alert_where_only_a_notice_stands()
    {
        var (tenant, rule) = await SeedAsync();
        var now = DateTime.UtcNow;
        await Instance(tenant).RecordUnevaluableNoticeAsync(Snapshot(tenant, rule), Version, now.AddHours(-1), CancellationToken.None);

        await using var db = Context(tenant);
        foreach (var source in new[] { ReportDataSources.AlertExcursions, ReportDataSources.AlertInstances })
        {
            (await ReportDataSources.All[source](db, null!, now.AddDays(-1), now.AddMinutes(1), TimeZoneInfo.Utc, CancellationToken.None))
                .Should().BeFalse(source);
        }
    }

    [Fact]
    public async Task A_notice_is_never_an_active_excursion()
    {
        var (tenant, rule) = await SeedAsync();
        var repository = Instance(tenant);

        var notice = (await repository.RecordUnevaluableNoticeAsync(Snapshot(tenant, rule), Version, At, CancellationToken.None))!;

        (await repository.CountActiveExcursionsAsync(CancellationToken.None)).Should().Be(0);
        (await repository.GetActiveAlertSnapshotsAsync(CancellationToken.None)).Should().BeEmpty();
        (await repository.GetExcursionRuleAsync(notice.ExcursionId, CancellationToken.None))!
            .IsUnevaluableNotice.Should().BeTrue();
        await using var db = Context(tenant);
        (await db.AlertExcursions.SingleAsync(e => e.Id == notice.ExcursionId)).EndedAt
            .Should().Be(DateTime.MaxValue, "a standing notice ends at infinity");
    }
}
