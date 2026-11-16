using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Reports;
using Nocturne.API.Reports.Datasets;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// The report datasets that read their tables in the request's own context (trackers, alert periods, alerts raised,
/// do not disturb, compression lows, lab HbA1c, body weight and sleep body readings), on PostgreSQL under the runtime
/// role and Row Level Security: each reads its rows, only its tenant's, and as much as the reader may see. A
/// history-clamped member reads the last 24 hours (and what is still running); a share reads none of a hidden table,
/// and the alert tables only because they are shared under alerts.read.
/// </summary>
[Trait("Category", "Integration")]
public class RecordDatasetPostgresTests(RecordDatasetPostgresTests.Database database)
    : IClassFixture<RecordDatasetPostgresTests.Database>
{
    private const string Everything =
        "glucose.read,treatments.read,devices.read,heartrate.read,stepcount.read,food.read,alerts.read,sleep.read,therapy.read";

    private static readonly string[] Scopes =
        [Scope.GlucoseRead, Scope.AlertsRead, Scope.SleepRead, Scope.TherapyRead, Scope.HeartRateRead];

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        /// <summary>Two tenants seeded alike, so a read that crossed tenants would count each row twice.</summary>
        public Guid Tenant { get; private set; }
        public Guid Other { get; private set; }
        public DateTimeOffset Now { get; private set; }

        public async Task InitializeAsync()
        {
            var db = await SharedPostgres.CreateMigratedDatabaseAsync("report_records");
            AppConnectionString = db.AppConnectionString;
            MigratorConnectionString = db.MigratorConnectionString;
            Now = DateTimeOffset.UtcNow;
            Tenant = await SeedTenantAsync();
            Other = await SeedTenantAsync();
        }

        public Task DisposeAsync() => Task.CompletedTask;

        public DbContextOptions<NocturneDbContext> Options =>
            new DbContextOptionsBuilder<NocturneDbContext>()
                .UseNpgsql(AppConnectionString)
                .AddInterceptors(new TenantConnectionInterceptor())
                .Options;

        private async Task<Guid> SeedTenantAsync()
        {
            var tenant = Guid.NewGuid();
            await using (var conn = new NpgsqlConnection(MigratorConnectionString))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                    VALUES (@id, @slug, 'report-records-test', true, now(), now())
                    """;
                cmd.Parameters.AddWithValue("id", tenant);
                cmd.Parameters.AddWithValue("slug", $"report-records-{tenant:N}");
                await cmd.ExecuteNonQueryAsync();
            }

            var now = Now.UtcDateTime;
            long Mills(DateTime at) => new DateTimeOffset(at).ToUnixTimeMilliseconds();
            await using var db = new NocturneDbContext(Options) { TenantId = tenant };

            var definition = new TrackerDefinitionEntity
            {
                Id = Guid.CreateVersion7(), CreatedBy = "owner", Name = "Site", Category = TrackerCategory.Cannula,
            };
            db.TrackerDefinitions.Add(definition);
            db.TrackerInstances.AddRange(
                new TrackerInstanceEntity { Id = Guid.CreateVersion7(), CreatedBy = "owner", DefinitionId = definition.Id, StartedAt = now.AddDays(-6) },
                new TrackerInstanceEntity
                {
                    Id = Guid.CreateVersion7(), CreatedBy = "owner", DefinitionId = definition.Id,
                    StartedAt = now.AddDays(-5), CompletedAt = now.AddDays(-2),
                },
                new TrackerInstanceEntity
                {
                    Id = Guid.CreateVersion7(), CreatedBy = "owner", DefinitionId = definition.Id,
                    StartedAt = now.AddDays(-3), CompletedAt = now.AddHours(-1),
                });

            var rule = new AlertRuleEntity { Id = Guid.CreateVersion7(), Name = "Low" };
            db.AlertRules.Add(rule);
            var open = new AlertExcursionEntity { Id = Guid.CreateVersion7(), AlertRuleId = rule.Id, StartedAt = now.AddDays(-3) };
            db.AlertExcursions.AddRange(
                open,
                new AlertExcursionEntity { Id = Guid.CreateVersion7(), AlertRuleId = rule.Id, StartedAt = now.AddDays(-4), EndedAt = now.AddDays(-2) },
                new AlertExcursionEntity { Id = Guid.CreateVersion7(), AlertRuleId = rule.Id, StartedAt = now.AddHours(-30), EndedAt = now.AddHours(-1) });
            db.AlertInstances.AddRange(
                new AlertInstanceEntity { Id = Guid.CreateVersion7(), AlertExcursionId = open.Id, TriggeredAt = now.AddDays(-3) },
                new AlertInstanceEntity
                {
                    Id = Guid.CreateVersion7(), AlertExcursionId = open.Id, TriggeredAt = now.AddDays(-4), ResolvedAt = now.AddDays(-2), Status = "resolved",
                },
                new AlertInstanceEntity
                {
                    Id = Guid.CreateVersion7(), AlertExcursionId = open.Id, TriggeredAt = now.AddHours(-30), ResolvedAt = now.AddHours(-1), Status = "resolved",
                });

            db.DndWindows.AddRange(
                new DndWindowEntity { Id = Guid.CreateVersion7(), Scope = DndScope.Lows, StartedAt = now.AddHours(-2), EndsAt = now.AddHours(1), CreatedAt = now },
                new DndWindowEntity
                {
                    Id = Guid.CreateVersion7(), Scope = DndScope.All, StartedAt = now.AddDays(-3), EndsAt = now.AddDays(-3).AddHours(8),
                    ClearedAt = now.AddDays(-3).AddHours(2), CreatedAt = now.AddDays(-3),
                });

            db.CompressionLowSuggestions.AddRange(
                new CompressionLowSuggestionEntity
                {
                    Id = Guid.CreateVersion7(), StartMills = Mills(now.AddHours(-3)), EndMills = Mills(now.AddHours(-1)), Confidence = 0.9,
                    NightOf = DateOnly.FromDateTime(now), CreatedAt = Mills(now), LowestGlucose = 52,
                },
                new CompressionLowSuggestionEntity
                {
                    Id = Guid.CreateVersion7(), StartMills = Mills(now.AddDays(-3).AddHours(-2)), EndMills = Mills(now.AddDays(-3)), Confidence = 0.8,
                    NightOf = DateOnly.FromDateTime(now.AddDays(-3)), CreatedAt = Mills(now), Status = "Dismissed",
                });

            db.LabHbA1cResults.AddRange(
                new LabHbA1cResultEntity { Id = Guid.CreateVersion7(), MeasuredAt = now.AddHours(-1), ValuePercent = 6.5, CreatedAt = now },
                new LabHbA1cResultEntity { Id = Guid.CreateVersion7(), MeasuredAt = now.AddDays(-3), ValuePercent = 7.1, CreatedAt = now });

            db.BodyWeights.AddRange(
                new BodyWeightEntity { Id = Guid.CreateVersion7(), Mills = Mills(now.AddHours(-1)), WeightKg = 70 },
                new BodyWeightEntity { Id = Guid.CreateVersion7(), Mills = Mills(now.AddDays(-3)), WeightKg = 71 });

            var lastNight = new SleepSessionEntity
            {
                Id = Guid.CreateVersion7(), StartTime = now.AddHours(-10), EndTime = now.AddHours(-2),
                Type = "Overnight", DetectionMethod = "Auto", Source = "Fitbit",
            };
            var threeNightsAgo = new SleepSessionEntity
            {
                Id = Guid.CreateVersion7(), StartTime = now.AddDays(-3).AddHours(-8), EndTime = now.AddDays(-3),
                Type = "Overnight", DetectionMethod = "Auto", Source = "Fitbit",
            };
            db.SleepSessions.AddRange(lastNight, threeNightsAgo);
            db.SleepBiometricSamples.AddRange(
                new SleepBiometricSampleEntity { Id = Guid.CreateVersion7(), SleepSessionId = lastNight.Id, Timestamp = now.AddHours(-3), HeartRate = 58 },
                new SleepBiometricSampleEntity { Id = Guid.CreateVersion7(), SleepSessionId = threeNightsAgo.Id, Timestamp = now.AddDays(-3).AddHours(-1), HeartRate = 61 });

            await db.SaveChangesAsync();
            return tenant;
        }
    }

    /// <summary>Each dataset under test and its rows for an owner, for a history-clamped member, and for a full-history share.</summary>
    public static TheoryData<string, int, int, int> Datasets => new()
    {
        { TrackersParameters.Name, 3, 2, 0 },
        { AlertExcursionsParameters.Name, 3, 2, 3 },
        { AlertInstancesParameters.Name, 3, 2, 3 },
        { DndWindowsParameters.Name, 2, 2, 0 },
        { CompressionLowsParameters.Name, 2, 1, 0 },
        { LabHbA1cParameters.Name, 2, 1, 0 },
        { BodyWeightParameters.Name, 2, 1, 0 },
        { SleepBiometricsParameters.Name, 2, 1, 0 },
    };

    private static readonly string[] Names =
    [
        TrackersParameters.Name, AlertExcursionsParameters.Name, AlertInstancesParameters.Name, DndWindowsParameters.Name,
        CompressionLowsParameters.Name, LabHbA1cParameters.Name, BodyWeightParameters.Name, SleepBiometricsParameters.Name,
    ];

    private static IReportDataset Dataset(string name) => name switch
    {
        TrackersParameters.Name => new TrackersDataset(),
        AlertExcursionsParameters.Name => new AlertExcursionsDataset(),
        AlertInstancesParameters.Name => new AlertInstancesDataset(),
        DndWindowsParameters.Name => new DndWindowsDataset(),
        CompressionLowsParameters.Name => new CompressionLowsDataset(),
        LabHbA1cParameters.Name => new LabHbA1cDataset(),
        BodyWeightParameters.Name => new BodyWeightDataset(),
        SleepBiometricsParameters.Name => new SleepBiometricsDataset(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private static ReportDatasetParameters Parameters(string name) => name switch
    {
        TrackersParameters.Name => new TrackersParameters(),
        AlertExcursionsParameters.Name => new AlertExcursionsParameters(),
        AlertInstancesParameters.Name => new AlertInstancesParameters(),
        DndWindowsParameters.Name => new DndWindowsParameters(),
        CompressionLowsParameters.Name => new CompressionLowsParameters(),
        LabHbA1cParameters.Name => new LabHbA1cParameters(),
        BodyWeightParameters.Name => new BodyWeightParameters(),
        SleepBiometricsParameters.Name => new SleepBiometricsParameters(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    /// <summary>The dataset's rows over the last week for <paramref name="tenant"/>, read as <paramref name="reader"/>.</summary>
    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsAsync(string name, Guid tenant, ICategoryReadContext? reader)
    {
        var seed = new NocturneDbContext(database.Options) { TenantId = tenant }.ReadingFrom(reader: reader);
        await using var services = new ServiceCollection().AddSingleton(seed.AsFactory()).BuildServiceProvider();
        var to = database.Now.AddMinutes(1);
        var output = await Dataset(name).RunAsync(
            new ReportDatasetRequest(Parameters(name), ReportPanelKind.Chart, to.AddDays(-7), to, false, new HashSet<string>(Scopes),
                services, new ReportReadLog(), HistoryClamped: reader?.IsHistoryClamped == true, Now: to, Zone: TimeZoneInfo.Utc),
            CancellationToken.None);
        await seed.DisposeAsync();
        return output.Rows;
    }

    private static CategoryReadContext Clamped()
    {
        var reader = new CategoryReadContext();
        reader.ClampMemberHistory();
        return reader;
    }

    private static CategoryReadContext Share()
    {
        var reader = new CategoryReadContext();
        reader.MarkShare();
        reader.SetVisibleCategories(Everything);
        reader.SetFullHistory(true);
        return reader;
    }

    [Theory]
    [MemberData(nameof(Datasets))]
    public async Task Each_reader_reads_its_tenants_rows_as_far_as_row_level_security_lets_it(string name, int owner, int clamped, int share)
    {
        (await RowsAsync(name, database.Tenant, null)).Should().HaveCount(owner, $"an owner reads the week of {name} of one tenant, not both seeded tenants'");
        (await RowsAsync(name, database.Other, null)).Should().HaveCount(owner, "the other tenant reads its own rows");
        (await RowsAsync(name, database.Tenant, Clamped())).Should().HaveCount(clamped, "a clamped member reads the last day and what is still running");
        (await RowsAsync(name, database.Tenant, Share())).Should().HaveCount(share, "a share reads no hidden table, and the alert tables under alerts.read");
    }

    [Fact]
    public async Task A_share_without_alerts_read_reads_no_alert_periods()
    {
        var reader = new CategoryReadContext();
        reader.MarkShare();
        reader.SetVisibleCategories("glucose.read");
        reader.SetFullHistory(true);
        (await RowsAsync(AlertExcursionsParameters.Name, database.Tenant, reader)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_tenant_with_no_records_reads_none()
    {
        var empty = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'report-records-empty', true, now(), now())
                """;
            cmd.Parameters.AddWithValue("id", empty);
            cmd.Parameters.AddWithValue("slug", $"report-records-{empty:N}");
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (var name in Names)
            (await RowsAsync(name, empty, null)).Should().BeEmpty(name);
    }
}
