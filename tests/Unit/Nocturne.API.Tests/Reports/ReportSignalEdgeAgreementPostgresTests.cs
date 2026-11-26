using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Reports;
using Nocturne.API.Reports.Datasets;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// A signal the workspace offers has something to draw: a span that ends exactly at the start of the window is neither
/// counted by <see cref="ReportSignalAvailability"/> nor read by the lane that draws the signal.
/// </summary>
[Trait("Category", "Integration")]
public class ReportSignalEdgeAgreementPostgresTests(ReportSignalEdgeAgreementPostgresTests.Database database)
    : IClassFixture<ReportSignalEdgeAgreementPostgresTests.Database>
{
    private const int Hours = 24;

    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset From = Now.AddHours(-Hours);

    private static readonly string[] Everything =
    [
        Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead, Scope.TherapyRead, Scope.HeartRateRead, Scope.StepCountRead,
        Scope.SleepRead, Scope.AlertsRead,
    ];

    private static readonly string[] SpanSignals =
    [
        "pumpModes", "overrides", "activities", "tempBasals", "profiles", "temporaryTargets", "pumpConnection", "dataExclusions",
        "motion", "sleepStages", "trackers", "doNotDisturb", "alertPeriods", "compressionLows",
    ];

    public static TheoryData<string> Signals => [.. SpanSignals];

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        /// <summary>One span of each signal, each ending exactly at the start of the window.</summary>
        public Guid EndingAtTheStart { get; private set; }

        /// <summary>One span of each signal, each ending a second after the start of the window.</summary>
        public Guid EndingAfterTheStart { get; private set; }

        public async Task InitializeAsync()
        {
            var db = await SharedPostgres.CreateMigratedDatabaseAsync("report_signal_edges");
            AppConnectionString = db.AppConnectionString;
            MigratorConnectionString = db.MigratorConnectionString;
            EndingAtTheStart = await SeedAsync(From.UtcDateTime);
            EndingAfterTheStart = await SeedAsync(From.UtcDateTime.AddSeconds(1));
        }

        public Task DisposeAsync() => Task.CompletedTask;

        public DbContextOptions<NocturneDbContext> Options => TestContexts.Options(AppConnectionString);

        private async Task<Guid> SeedAsync(DateTime end)
        {
            var tenant = Guid.NewGuid();
            await using (var conn = new NpgsqlConnection(MigratorConnectionString))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                    VALUES (@id, @slug, 'report-signal-edges', true, now(), now())
                    """;
                cmd.Parameters.AddWithValue("id", tenant);
                cmd.Parameters.AddWithValue("slug", $"report-signal-edges-{tenant:N}");
                await cmd.ExecuteNonQueryAsync();
            }

            var start = From.UtcDateTime.AddHours(-2);
            long Mills(DateTime at) => new DateTimeOffset(at).ToUnixTimeMilliseconds();
            await using var seed = new NocturneDbContext(Options) { TenantId = tenant };

            foreach (var (category, state) in new[]
            {
                (nameof(StateSpanCategory.PumpMode), "Automatic"), (nameof(StateSpanCategory.Override), "Custom"),
                (nameof(StateSpanCategory.Exercise), "Active"), (nameof(StateSpanCategory.TemporaryTarget), "Active"),
                (nameof(StateSpanCategory.PumpConnectivity), "BluetoothOff"), (nameof(StateSpanCategory.DataExclusion), "Excluded"),
                (nameof(StateSpanCategory.Motion), "Walking"),
            })
            {
                seed.StateSpans.Add(new StateSpanEntity
                {
                    Id = Guid.CreateVersion7(), Category = category, State = state, Source = "a", StartTimestamp = start, EndTimestamp = end,
                });
            }

            seed.TempBasals.Add(new TempBasalEntity
            {
                Id = Guid.CreateVersion7(), Timestamp = start, EndTimestamp = end, Rate = 1.2, Origin = "Algorithm", Device = "pump", DataSource = "test",
            });
            seed.ProfileActivations.Add(new ProfileActivationEntity
            {
                Id = Guid.CreateVersion7(), Timestamp = start, EndTimestamp = end, Phase = "Requested", Origin = "Uploader", BaseProfileName = "Weekend",
            });

            var session = new SleepSessionEntity
            {
                Id = Guid.CreateVersion7(), StartTime = start, EndTime = end, Type = "Overnight", DetectionMethod = "Auto", Source = "Fitbit",
            };
            seed.SleepSessions.Add(session);
            seed.SleepStages.Add(new SleepStageEntity
            {
                Id = Guid.CreateVersion7(), SleepSessionId = session.Id, StartTime = start, EndTime = end, Stage = "Deep",
            });

            var definition = new TrackerDefinitionEntity
            {
                Id = Guid.CreateVersion7(), CreatedBy = "owner", Name = "Site", Category = TrackerCategory.Cannula,
            };
            seed.TrackerDefinitions.Add(definition);
            seed.TrackerInstances.Add(new TrackerInstanceEntity
            {
                Id = Guid.CreateVersion7(), CreatedBy = "owner", DefinitionId = definition.Id, StartedAt = start, CompletedAt = end,
            });

            seed.DndWindows.Add(new DndWindowEntity
            {
                Id = Guid.CreateVersion7(), Scope = DndScope.All, StartedAt = start, EndsAt = end, CreatedAt = start,
            });

            var rule = new AlertRuleEntity { Id = Guid.CreateVersion7(), Name = "Low" };
            seed.AlertRules.Add(rule);
            seed.AlertExcursions.Add(new AlertExcursionEntity
            {
                Id = Guid.CreateVersion7(), AlertRuleId = rule.Id, StartedAt = start, EndedAt = end,
            });

            seed.CompressionLowSuggestions.Add(new CompressionLowSuggestionEntity
            {
                Id = Guid.CreateVersion7(), StartMills = Mills(start), EndMills = Mills(end), Confidence = 0.9,
                NightOf = DateOnly.FromDateTime(start), CreatedAt = Mills(start), LowestGlucose = 52,
            });

            await seed.SaveChangesAsync();
            return tenant;
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> AvailabilityAsync(Guid tenant)
    {
        await using var context = new NocturneDbContext(database.Options) { TenantId = tenant }.ReadingFrom();
        var factory = context.AsFactory();
        var availability = new ReportSignalAvailability(
            ReportSpecValidatorTests.Registry, factory, ReportSignalAvailabilityPostgresTests.Repositories(factory),
            new FakeTimeProvider(Now), ReportSignalAvailabilityPostgresTests.Clock(TimeZoneInfo.Utc));
        var result = await availability.ForRangeAsync(Hours, new HashSet<string>(Everything), historyClamped: false, CancellationToken.None);
        return result.Signals.ToDictionary(s => s.Signal, s => s.State);
    }

    private async Task<int> DrawnAsync(IReportDataset dataset, ReportDatasetParameters parameters, Guid tenant)
    {
        await using var context = new NocturneDbContext(database.Options) { TenantId = tenant }.ReadingFrom();
        var factory = context.AsFactory();
        var states = new StateSpanRepository(
            factory, Mock.Of<IDuplicateLinker>(), new SystemAuditContext(), NullLogger<StateSpanRepository>.Instance);
        await using var services = new ServiceCollection().AddSingleton(factory)
            .AddSingleton<Nocturne.Core.Contracts.Alerts.IDndWindowRepository>(new DndWindowRepository(factory))
            .AddSingleton<ISleepSessionRepository>(new SleepSessionRepository(factory))
            .AddSingleton<ITrackerRepository>(new TrackerRepository(factory))
            .AddSingleton<Nocturne.Core.Contracts.Glucose.IStateSpanService>(
                new StateSpanService(states, NullLogger<StateSpanService>.Instance))
            .BuildServiceProvider();
        var output = await dataset.RunAsync(
            new ReportDatasetRequest(parameters, ReportPanelKind.Chart, From, Now, false, new HashSet<string>(Everything),
                services, new ReportReadLog(), HistoryClamped: false, Now: Now, Zone: TimeZoneInfo.Utc, Whole: false),
            CancellationToken.None);
        return output.Rows.Count;
    }

    [Theory]
    [MemberData(nameof(Signals))]
    public async Task A_span_ending_exactly_at_the_window_start_is_not_available(string signal)
    {
        (await AvailabilityAsync(database.EndingAtTheStart))[signal].Should().Be(ReportSignalAvailability.None);
    }

    [Theory]
    [MemberData(nameof(Signals))]
    public async Task A_span_ending_after_the_window_start_is_available(string signal)
    {
        (await AvailabilityAsync(database.EndingAfterTheStart))[signal].Should().Be(ReportSignalAvailability.Data);
    }

    [Fact]
    public async Task Alert_periods_are_neither_available_nor_drawn_for_a_period_ending_at_the_window_start()
    {
        (await AvailabilityAsync(database.EndingAtTheStart))["alertPeriods"].Should().Be(ReportSignalAvailability.None);
        (await DrawnAsync(new AlertExcursionsDataset(), new AlertExcursionsParameters(), database.EndingAtTheStart)).Should().Be(0);
    }

    [Fact]
    public async Task Alert_periods_are_available_and_drawn_for_a_period_ending_after_the_window_start()
    {
        (await AvailabilityAsync(database.EndingAfterTheStart))["alertPeriods"].Should().Be(ReportSignalAvailability.Data);
        (await DrawnAsync(new AlertExcursionsDataset(), new AlertExcursionsParameters(), database.EndingAfterTheStart)).Should().Be(1);
    }

    [Fact]
    public async Task Compression_lows_are_neither_available_nor_drawn_for_one_ending_at_the_window_start()
    {
        (await AvailabilityAsync(database.EndingAtTheStart))["compressionLows"].Should().Be(ReportSignalAvailability.None);
        (await DrawnAsync(new CompressionLowsDataset(), new CompressionLowsParameters(), database.EndingAtTheStart)).Should().Be(0);
    }

    [Fact]
    public async Task Compression_lows_are_available_and_drawn_for_one_ending_after_the_window_start()
    {
        (await AvailabilityAsync(database.EndingAfterTheStart))["compressionLows"].Should().Be(ReportSignalAvailability.Data);
        (await DrawnAsync(new CompressionLowsDataset(), new CompressionLowsParameters(), database.EndingAfterTheStart)).Should().Be(1);
    }

    [Fact]
    public async Task Do_not_disturb_trackers_and_sleep_stages_are_not_drawn_for_a_span_ending_at_the_window_start()
    {
        var tenant = database.EndingAtTheStart;

        (await DrawnAsync(new DndWindowsDataset(), new DndWindowsParameters(), tenant)).Should().Be(0);
        (await DrawnAsync(new TrackersDataset(), new TrackersParameters(), tenant)).Should().Be(0);
        (await DrawnAsync(new SleepStagesDataset(), new SleepStagesParameters(), tenant)).Should().Be(0);
    }

    [Fact]
    public async Task Do_not_disturb_trackers_and_sleep_stages_are_drawn_for_a_span_ending_after_the_window_start()
    {
        var tenant = database.EndingAfterTheStart;

        (await DrawnAsync(new DndWindowsDataset(), new DndWindowsParameters(), tenant)).Should().Be(1);
        (await DrawnAsync(new TrackersDataset(), new TrackersParameters(), tenant)).Should().Be(1);
        (await DrawnAsync(new SleepStagesDataset(), new SleepStagesParameters(), tenant)).Should().Be(1);
    }

    private static readonly ReportSpanKind[] ChartKinds =
        [ReportSpanKind.PumpMode, ReportSpanKind.Profile, ReportSpanKind.Override, ReportSpanKind.Activity, ReportSpanKind.TempBasal];

    private static DashboardChartData ChartSpansEndingAt(DateTimeOffset end)
    {
        List<ChartStateSpanDto> One() =>
        [
            new() { State = "Active", StartMills = From.AddHours(-2).ToUnixTimeMilliseconds(), EndMills = end.ToUnixTimeMilliseconds() },
        ];
        return new DashboardChartData
        {
            PumpModeSpans = One(), ProfileSpans = One(), OverrideSpans = One(), ActivitySpans = One(), TempBasalSpans = One(),
        };
    }

    [Fact]
    public void The_chart_kinds_are_not_drawn_for_a_span_ending_at_the_window_start()
    {
        StateSpansDataset.Spans(ChartSpansEndingAt(From), ChartKinds, From.ToUnixTimeMilliseconds(), Now.ToUnixTimeMilliseconds())
            .Should().BeEmpty();
    }

    [Fact]
    public void The_chart_kinds_are_drawn_for_a_span_ending_after_the_window_start()
    {
        StateSpansDataset.Spans(
                ChartSpansEndingAt(From.AddSeconds(1)), ChartKinds, From.ToUnixTimeMilliseconds(), Now.ToUnixTimeMilliseconds())
            .Select(span => span.Kind).Should().BeEquivalentTo(ChartKinds);
    }

    [Fact]
    public async Task The_stored_state_span_kinds_are_drawn_only_for_a_span_ending_after_the_window_start()
    {
        var kinds = new StateSpansParameters
        {
            Kinds = [ReportSpanKind.TemporaryTarget, ReportSpanKind.PumpConnectivity, ReportSpanKind.DataExclusion, ReportSpanKind.Motion],
        };

        (await DrawnAsync(new StateSpansDataset(), kinds, database.EndingAtTheStart)).Should().Be(0);
        (await DrawnAsync(new StateSpansDataset(), kinds, database.EndingAfterTheStart)).Should().Be(4);
    }
}
