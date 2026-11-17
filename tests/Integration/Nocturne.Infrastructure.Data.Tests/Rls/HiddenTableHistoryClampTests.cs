using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// The 24-hour member clamp on the hidden time-series tables (#2362), read through the live
/// <c>share_category_read</c> policies as the app role: a clamped member reads only rows inside the
/// window, plus spans still open; an unclamped reader reads every row; no share reads any of them.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class HiddenTableHistoryClampTests(RlsCompletenessFixture fx)
{
    private const string EveryCategory =
        "glucose.read,treatments.read,devices.read,heartrate.read,stepcount.read,food.read";

    public static TheoryData<string> Tables => new(TableNames);

    private static readonly string[] TableNames =
    [
        "sleep_sessions",
        "sleep_stages",
        "sleep_biometric_samples",
        "body_weights",
        "lab_hba1c_results",
        "alert_excursions",
        "alert_instances",
        "system_events",
        "in_app_notifications",
        "tracker_instances",
        "treatment_foods",
        "device_clock_observations",
        "alert_excursion_mutes",
        "alert_deliveries",
        "discrepancy_analyses",
        "discrepancy_details",
    ];

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task ClampedMember_ReadsOnlyRowsInsideTheWindowOrStillOpen(string table)
    {
        var (tenant, rows) = await SeedAsync();

        await using var conn = await fx.OpenAppConnectionAsync();
        await SetReaderAsync(conn, tenant, isShare: false, historyClamped: "true");

        (await IdsAsync(conn, table, tenant)).Should().BeEquivalentTo(
            rows[table].Where(r => r.VisibleWhenClamped).Select(r => r.Id),
            $"a clamped member reads {table} only inside the last 24 hours");
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task UnclampedReader_ReadsEveryRow(string table)
    {
        var (tenant, rows) = await SeedAsync();

        foreach (var historyClamped in new[] { "false", null })
        {
            await using var conn = await fx.OpenAppConnectionAsync();
            await SetReaderAsync(conn, tenant, isShare: false, historyClamped: historyClamped);
            (await IdsAsync(conn, table, tenant)).Should().BeEquivalentTo(
                rows[table].Select(r => r.Id), $"an owner reads all of {table} (history_clamped={historyClamped ?? "unset"})");
        }
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task Share_ReadsNoRows(string table)
    {
        var (tenant, _) = await SeedAsync();

        await using var conn = await fx.OpenAppConnectionAsync();
        foreach (var fullHistory in new[] { false, true })
        {
            await SetReaderAsync(conn, tenant, isShare: true, visibleCategories: EveryCategory, fullHistory: fullHistory);
            (await IdsAsync(conn, table, tenant)).Should().BeEmpty($"{table} has no governing scope, so no share reads it");
        }
    }

    [Fact]
    public async Task ClampedMember_ReadsAFoodLineOnceItsCarbIntakeMovesIntoTheWindow()
    {
        var (tenant, rows) = await SeedAsync();
        var hidden = rows["treatment_foods"].Single(r => !r.VisibleWhenClamped).Id;

        await using (var owner = await fx.OpenAppConnectionAsync())
        {
            await SetReaderAsync(owner, tenant, isShare: false);
            await using var cmd = owner.CreateCommand();
            cmd.CommandText = """
                UPDATE carb_intakes SET timestamp = now()
                WHERE id = (SELECT carb_intake_id FROM treatment_foods WHERE id = @id)
                """;
            cmd.Parameters.AddWithValue("id", hidden);
            (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
        }

        await using var conn = await fx.OpenAppConnectionAsync();
        await SetReaderAsync(conn, tenant, isShare: false, historyClamped: "true");
        (await IdsAsync(conn, "treatment_foods", tenant)).Should().Contain(hidden);
    }

    [Fact]
    public void EveryTableUnderTest_IsClassifiedForTheMemberClamp()
    {
        foreach (var table in TableNames)
        {
            // The alert spans are shared under alerts.read, which EveryCategory leaves out, and
            // clamped for members by overlap exactly like the hidden spans.
            if (!ShareDataCategories.GovernedSpanEndColumns.ContainsKey(table))
                ShareDataCategories.GoverningScopeFor(table).Should().BeNull();
            (ShareDataCategories.RecencyColumnFor(table) ?? ShareDataCategories.SpanEndColumnFor(table)
                ?? ShareDataCategories.ParentRecencyFor(table)?.Table).Should().NotBeNull(table);
        }
    }

    [Fact]
    public async Task ClampedMember_CatchesUpTheFailedDeliveriesOfAnOpenAlertAndMarksThemDelivered()
    {
        var (tenant, _) = await SeedAsync();

        await using var conn = await fx.OpenAppConnectionAsync();
        await SetReaderAsync(conn, tenant, isShare: false, historyClamped: "true");
        await using var db = new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>().UseNpgsql(conn).Options) { TenantId = tenant };

        var failed = await db.AlertDeliveries
            .Include(d => d.AlertInstance)
                .ThenInclude(i => i!.AlertExcursion)
            .Where(d => d.ChannelType == ChannelType.HomeAssistant
                        && d.Destination == HomeAssistantInstance
                        && d.Status == "failed"
                        && d.AlertInstance != null
                        && d.AlertInstance.AlertExcursion != null
                        && d.AlertInstance.AlertExcursion.EndedAt == null)
            .ToListAsync();

        var delivery = failed.Should().ContainSingle("the failed delivery of the alert open for three days").Subject;
        delivery.Status = "delivered";
        delivery.DeliveredAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        (await db.AlertDeliveries.AsNoTracking().Where(d => d.Status == "delivered").Select(d => d.Id).ToListAsync())
            .Should().Contain(delivery.Id);
    }

    private const string HomeAssistantInstance = "ha-instance";

    private sealed record Row(Guid Id, bool VisibleWhenClamped);

    private async Task<(Guid Tenant, Dictionary<string, List<Row>> Rows)> SeedAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var migrator = await fx.OpenMigratorConnectionAsync())
        await using (var cmd = migrator.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@tid, @slug, 'hidden-clamp-test', true, now(), now())
                """;
            cmd.Parameters.AddWithValue("tid", tenant);
            cmd.Parameters.AddWithValue("slug", $"hidden-clamp-{tenant:N}");
            await cmd.ExecuteNonQueryAsync();
        }

        var rows = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
        Guid Track(string table, bool visibleWhenClamped)
        {
            var id = Guid.CreateVersion7();
            if (!rows.TryGetValue(table, out var list))
                rows[table] = list = [];
            list.Add(new Row(id, visibleWhenClamped));
            return id;
        }

        var now = DateTime.UtcNow;
        static long Mills(DateTime at) => new DateTimeOffset(at).ToUnixTimeMilliseconds();

        await using var conn = await fx.OpenAppConnectionAsync();
        await SetReaderAsync(conn, tenant, isShare: false);
        await using var db = new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>().UseNpgsql(conn).Options) { TenantId = tenant };

        var lastNight = new SleepSessionEntity
        {
            Id = Track("sleep_sessions", true), StartTime = now.AddHours(-10), EndTime = now.AddHours(-2),
            Type = "Overnight", DetectionMethod = "Auto", Source = "Fitbit",
        };
        var startedBeforeTheWindow = new SleepSessionEntity
        {
            Id = Track("sleep_sessions", true), StartTime = now.AddHours(-30), EndTime = now.AddHours(-20),
            Type = "Overnight", DetectionMethod = "Auto", Source = "Fitbit",
        };
        var threeNightsAgo = new SleepSessionEntity
        {
            Id = Track("sleep_sessions", false), StartTime = now.AddDays(-3).AddHours(-8), EndTime = now.AddDays(-3),
            Type = "Overnight", DetectionMethod = "Auto", Source = "Fitbit",
        };
        db.SleepSessions.AddRange(lastNight, startedBeforeTheWindow, threeNightsAgo);
        db.SleepStages.AddRange(
            new SleepStageEntity
            {
                Id = Track("sleep_stages", true), SleepSessionId = lastNight.Id,
                StartTime = now.AddHours(-3), EndTime = now.AddHours(-2), Stage = "Light",
            },
            new SleepStageEntity
            {
                Id = Track("sleep_stages", true), SleepSessionId = startedBeforeTheWindow.Id,
                StartTime = now.AddHours(-30), EndTime = now.AddHours(-25), Stage = "Deep",
            },
            new SleepStageEntity
            {
                Id = Track("sleep_stages", false), SleepSessionId = threeNightsAgo.Id,
                StartTime = now.AddDays(-3).AddHours(-1), EndTime = now.AddDays(-3), Stage = "Deep",
            });
        db.SleepBiometricSamples.AddRange(
            new SleepBiometricSampleEntity { Id = Track("sleep_biometric_samples", true), SleepSessionId = lastNight.Id, Timestamp = now.AddHours(-3), HeartRate = 58 },
            new SleepBiometricSampleEntity { Id = Track("sleep_biometric_samples", true), SleepSessionId = startedBeforeTheWindow.Id, Timestamp = now.AddHours(-29), HeartRate = 55 },
            new SleepBiometricSampleEntity { Id = Track("sleep_biometric_samples", false), SleepSessionId = threeNightsAgo.Id, Timestamp = now.AddDays(-3), HeartRate = 61 });

        db.BodyWeights.AddRange(
            new BodyWeightEntity { Id = Track("body_weights", true), Mills = Mills(now.AddHours(-1)), WeightKg = 70 },
            new BodyWeightEntity { Id = Track("body_weights", false), Mills = Mills(now.AddDays(-3)), WeightKg = 71 });

        db.LabHbA1cResults.AddRange(
            new LabHbA1cResultEntity { Id = Track("lab_hba1c_results", true), MeasuredAt = now.AddHours(-1), ValuePercent = 6.5, CreatedAt = now },
            new LabHbA1cResultEntity { Id = Track("lab_hba1c_results", false), MeasuredAt = now.AddDays(-90), ValuePercent = 7.1, CreatedAt = now });

        var rule = new AlertRuleEntity { Id = Guid.CreateVersion7(), Name = "low" };
        db.AlertRules.Add(rule);
        var openSinceThreeDays = new AlertExcursionEntity
        {
            Id = Track("alert_excursions", true), AlertRuleId = rule.Id, StartedAt = now.AddDays(-3),
        };
        var endedTwoDaysAgo = new AlertExcursionEntity
        {
            Id = Track("alert_excursions", false), AlertRuleId = rule.Id,
            StartedAt = now.AddDays(-4), EndedAt = now.AddDays(-2),
        };
        var endedAnHourAgo = new AlertExcursionEntity
        {
            Id = Track("alert_excursions", true), AlertRuleId = rule.Id,
            StartedAt = now.AddHours(-30), EndedAt = now.AddHours(-1),
        };
        db.AlertExcursions.AddRange(openSinceThreeDays, endedTwoDaysAgo, endedAnHourAgo);
        var openInstance = new AlertInstanceEntity
        {
            Id = Track("alert_instances", true), AlertExcursionId = openSinceThreeDays.Id, TriggeredAt = now.AddDays(-3),
        };
        var resolvedTwoDaysAgo = new AlertInstanceEntity
        {
            Id = Track("alert_instances", false), AlertExcursionId = openSinceThreeDays.Id,
            TriggeredAt = now.AddDays(-4), ResolvedAt = now.AddDays(-2), Status = "resolved",
        };
        var resolvedAnHourAgo = new AlertInstanceEntity
        {
            Id = Track("alert_instances", true), AlertExcursionId = openSinceThreeDays.Id,
            TriggeredAt = now.AddHours(-30), ResolvedAt = now.AddHours(-1), Status = "resolved",
        };
        db.AlertInstances.AddRange(openInstance, resolvedTwoDaysAgo, resolvedAnHourAgo);

        var member = new SubjectEntity { Id = Guid.CreateVersion7(), Name = $"hidden-clamp-{tenant:N}" };
        db.Subjects.Add(member);
        db.AlertExcursionMutes.AddRange(
            new AlertExcursionMuteEntity { Id = Track("alert_excursion_mutes", true), SubjectId = member.Id, AlertExcursionId = openSinceThreeDays.Id, CreatedAt = now.AddDays(-3) },
            new AlertExcursionMuteEntity { Id = Track("alert_excursion_mutes", false), SubjectId = member.Id, AlertExcursionId = endedTwoDaysAgo.Id, CreatedAt = now.AddDays(-4) },
            new AlertExcursionMuteEntity { Id = Track("alert_excursion_mutes", true), SubjectId = member.Id, AlertExcursionId = endedAnHourAgo.Id, CreatedAt = now.AddHours(-30) });

        AlertDeliveryEntity Delivery(AlertInstanceEntity instance, bool visibleWhenClamped, string status) => new()
        {
            Id = Track("alert_deliveries", visibleWhenClamped), AlertInstanceId = instance.Id,
            ChannelType = ChannelType.HomeAssistant, Destination = HomeAssistantInstance,
            Payload = "{}", Status = status, CreatedAt = instance.TriggeredAt,
        };
        db.AlertDeliveries.AddRange(
            Delivery(openInstance, true, "pending"),
            Delivery(openInstance, true, "failed"),
            Delivery(resolvedTwoDaysAgo, false, "pending"),
            Delivery(resolvedAnHourAgo, true, "delivered"));

        DiscrepancyAnalysisEntity Analysis(DateTime at, bool visibleWhenClamped) => new()
        {
            Id = Track("discrepancy_analyses", visibleWhenClamped), TraceId = "trace", AnalysisTimestamp = at,
            RequestMethod = "GET", RequestPath = "/api/v1/entries", OverallMatch = ResponseMatchType.MinorDifferences,
            Discrepancies =
            [
                new DiscrepancyDetailEntity
                {
                    Id = Track("discrepancy_details", visibleWhenClamped), DiscrepancyType = DiscrepancyType.NumericValue,
                    Severity = DiscrepancySeverity.Minor, Field = "sgv", NightscoutValue = "120", NocturneValue = "121",
                    Description = "differs", RecordedAt = at,
                },
            ],
        };
        db.DiscrepancyAnalyses.AddRange(Analysis(now.AddHours(-1), true), Analysis(now.AddDays(-3), false));

        db.SystemEvents.AddRange(
            new SystemEventEntity { Id = Track("system_events", true), EventType = "Info", Category = "Pump", Mills = Mills(now.AddHours(-1)) },
            new SystemEventEntity { Id = Track("system_events", false), EventType = "Info", Category = "Pump", Mills = Mills(now.AddDays(-3)) });

        db.InAppNotifications.AddRange(
            new InAppNotificationEntity { Id = Track("in_app_notifications", true), UserId = "member", Type = "info", Title = "now", CreatedAt = now.AddHours(-1) },
            new InAppNotificationEntity { Id = Track("in_app_notifications", true), UserId = "member", Type = "alert.firing", Title = "still in the inbox, raised three days ago", CreatedAt = now.AddDays(-3) },
            new InAppNotificationEntity { Id = Track("in_app_notifications", false), UserId = "member", Type = "glucose.compression_low_review", Title = "never archived, raised three months ago", CreatedAt = now.AddDays(-90) },
            new InAppNotificationEntity
            {
                Id = Track("in_app_notifications", false), UserId = "member", Type = "info", Title = "archived two days ago",
                CreatedAt = now.AddDays(-4), IsArchived = true, ArchivedAt = now.AddDays(-2),
            },
            new InAppNotificationEntity
            {
                Id = Track("in_app_notifications", true), UserId = "member", Type = "info", Title = "archived an hour ago",
                CreatedAt = now.AddDays(-4), IsArchived = true, ArchivedAt = now.AddHours(-1),
            });

        var definition = new TrackerDefinitionEntity { Id = Guid.CreateVersion7(), CreatedBy = "member", Name = "site" };
        db.TrackerDefinitions.Add(definition);
        db.TrackerInstances.AddRange(
            new TrackerInstanceEntity { Id = Track("tracker_instances", true), CreatedBy = "member", DefinitionId = definition.Id, StartedAt = now.AddDays(-10) },
            new TrackerInstanceEntity
            {
                Id = Track("tracker_instances", false), CreatedBy = "member", DefinitionId = definition.Id,
                StartedAt = now.AddDays(-5), CompletedAt = now.AddDays(-2),
            },
            new TrackerInstanceEntity
            {
                Id = Track("tracker_instances", true), CreatedBy = "member", DefinitionId = definition.Id,
                StartedAt = now.AddDays(-3), CompletedAt = now.AddHours(-1),
            });

        var lunch = new CarbIntakeEntity { Id = Guid.CreateVersion7(), Timestamp = now.AddHours(-1), Carbs = 40 };
        var oldDinner = new CarbIntakeEntity { Id = Guid.CreateVersion7(), Timestamp = now.AddDays(-3), Carbs = 60 };
        db.CarbIntakes.AddRange(lunch, oldDinner);
        db.TreatmentFoods.AddRange(
            new TreatmentFoodEntity { Id = Track("treatment_foods", true), CarbIntakeId = lunch.Id, Portions = 1, Carbs = 40 },
            new TreatmentFoodEntity { Id = Track("treatment_foods", false), CarbIntakeId = oldDinner.Id, Portions = 1, Carbs = 60 });

        db.DeviceClockObservations.AddRange(
            new DeviceClockObservationEntity { Id = Track("device_clock_observations", true), Connector = "glooko", ObservedAt = now.AddHours(-1) },
            new DeviceClockObservationEntity { Id = Track("device_clock_observations", false), Connector = "glooko", ObservedAt = now.AddDays(-3) });

        await db.SaveChangesAsync();
        return (tenant, rows);
    }

    private static async Task<List<Guid>> IdsAsync(NpgsqlConnection conn, string table, Guid tenant)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id FROM {table} WHERE tenant_id = @tid";
        cmd.Parameters.AddWithValue("tid", tenant);
        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetGuid(0));
        return ids;
    }

    /// <param name="historyClamped">The <c>app.history_clamped</c> value, or null to leave it unset.</param>
    private static async Task SetReaderAsync(
        NpgsqlConnection conn, Guid tenant, bool isShare, string visibleCategories = "", bool fullHistory = false,
        string? historyClamped = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT set_config('app.current_tenant_id', @tid, false), " +
            "set_config('app.is_share', @share, false), " +
            "set_config('app.visible_categories', @cats, false), " +
            "set_config('app.share_full_history', @full, false)" +
            (historyClamped is null ? "" : ", set_config('app.history_clamped', @clamped, false)");
        cmd.Parameters.AddWithValue("tid", tenant.ToString());
        cmd.Parameters.AddWithValue("share", isShare ? "true" : "false");
        cmd.Parameters.AddWithValue("cats", visibleCategories);
        cmd.Parameters.AddWithValue("full", fullHistory ? "true" : "false");
        if (historyClamped is not null)
            cmd.Parameters.AddWithValue("clamped", historyClamped);
        await cmd.ExecuteNonQueryAsync();
    }
}
