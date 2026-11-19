using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Reports;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// Signal availability on PostgreSQL under the runtime role and Row Level Security: a signal has data only from the
/// sources the caller holds the scopes of, a history-clamped caller learns nothing older than the last 24 hours, and a
/// tenant sees only its own rows.
/// </summary>
[Trait("Category", "Integration")]
public class ReportSignalAvailabilityPostgresTests(ReportSignalAvailabilityPostgresTests.Database database)
    : IClassFixture<ReportSignalAvailabilityPostgresTests.Database>
{
    private static readonly string[] Everything =
    [
        Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead, Scope.TherapyRead, Scope.HeartRateRead, Scope.StepCountRead,
        Scope.SleepRead, Scope.AlertsRead,
    ];

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        /// <summary>A loop snapshot two hours ago, a profile activation an hour ago and one glucose reading three days ago.</summary>
        public Guid Tenant { get; private set; }

        /// <summary>No rows.</summary>
        public Guid Other { get; private set; }

        public async Task InitializeAsync()
        {
            var db = await SharedPostgres.CreateMigratedDatabaseAsync("report_availability");
            AppConnectionString = db.AppConnectionString;
            MigratorConnectionString = db.MigratorConnectionString;
            Tenant = await TenantAsync();
            Other = await TenantAsync();

            var now = DateTime.UtcNow;
            await using var seed = new NocturneDbContext(Options) { TenantId = Tenant };
            seed.ApsSnapshots.Add(new ApsSnapshotEntity { Id = Guid.CreateVersion7(), Timestamp = now.AddHours(-2), AidAlgorithm = "OpenAps" });
            seed.ProfileActivations.Add(new ProfileActivationEntity
            {
                Id = Guid.CreateVersion7(), Timestamp = now.AddHours(-1), Phase = "Requested", Origin = "Uploader", BaseProfileName = "Weekend",
            });
            seed.SensorGlucose.Add(new SensorGlucoseEntity { Id = Guid.CreateVersion7(), Timestamp = now.AddDays(-3), Mgdl = 120 });
            await seed.SaveChangesAsync();
        }

        public Task DisposeAsync() => Task.CompletedTask;

        public DbContextOptions<NocturneDbContext> Options =>
            new DbContextOptionsBuilder<NocturneDbContext>()
                .UseNpgsql(AppConnectionString)
                .AddInterceptors(new TenantConnectionInterceptor())
                .Options;

        private async Task<Guid> TenantAsync()
        {
            var tenant = Guid.NewGuid();
            await using var conn = new NpgsqlConnection(MigratorConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'report-availability-test', true, now(), now())
                """;
            cmd.Parameters.AddWithValue("id", tenant);
            cmd.Parameters.AddWithValue("slug", $"report-availability-{tenant:N}");
            await cmd.ExecuteNonQueryAsync();
            return tenant;
        }
    }

    /// <summary>The tenant clock a test resolves, with the tenant's zone <paramref name="zone"/>.</summary>
    internal static TenantClockResolver Clock(TimeZoneInfo zone)
    {
        var therapy = new Mock<ITherapySettingsResolver>();
        therapy.Setup(t => t.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(zone.Id);
        return new TenantClockResolver(therapy.Object, new CategoryReadContext(), new ShareClockDouble(therapy.Object), NullLogger<TenantClockResolver>.Instance);
    }

    /// <summary>Each signal's state over the last week for <paramref name="tenant"/>, read as <paramref name="reader"/>.</summary>
    private async Task<IReadOnlyDictionary<string, string>> StatesAsync(
        Guid tenant, string[] scopes, ICategoryReadContext? reader = null, bool historyClamped = false)
    {
        await using var context = new NocturneDbContext(database.Options) { TenantId = tenant }.ReadingFrom(reader: reader);
        var availability = new ReportSignalAvailability(ReportSpecValidatorTests.Registry, context.AsFactory(), TimeProvider.System, Clock(TimeZoneInfo.Utc));
        var result = await availability.ForRangeAsync(168, new HashSet<string>(scopes), historyClamped, CancellationToken.None);
        return result.Signals.ToDictionary(s => s.Signal, s => s.State);
    }

    private static CategoryReadContext Clamped()
    {
        var reader = new CategoryReadContext();
        reader.ClampMemberHistory();
        return reader;
    }

    [Fact]
    public async Task An_owner_finds_data_in_each_source_the_tenant_holds()
    {
        var states = await StatesAsync(database.Tenant, Everything);

        states["glucose"].Should().Be(ReportSignalAvailability.Data);
        states["iob"].Should().Be(ReportSignalAvailability.Data, "the loop's snapshots hold insulin on board");
        states["basalScheduled"].Should().Be(ReportSignalAvailability.Data, "a profile activation sets the scheduled basal");
        states["heartRate"].Should().Be(ReportSignalAvailability.None);
    }

    [Fact]
    public async Task A_scoped_credential_is_not_told_of_a_source_whose_scope_it_lacks()
    {
        var withoutDevices = await StatesAsync(database.Tenant, [Scope.GlucoseRead, Scope.TreatmentsRead, Scope.TherapyRead]);
        var withoutTherapy = await StatesAsync(database.Tenant, [Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead]);

        withoutDevices["iob"].Should().Be(ReportSignalAvailability.None, "the loop's snapshots need devices.read, so they are not asked");
        withoutDevices["pumpBattery"].Should().Be(ReportSignalAvailability.NoAccess);
        withoutTherapy["iob"].Should().Be(ReportSignalAvailability.Data);
        withoutTherapy["basal"].Should().Be(ReportSignalAvailability.None, "the profile activations need therapy.read, so they are not asked");
        withoutTherapy["profiles"].Should().Be(ReportSignalAvailability.NoAccess);
    }

    [Fact]
    public async Task A_clamped_member_learns_nothing_older_than_the_last_24_hours()
    {
        var states = await StatesAsync(database.Tenant, Everything, Clamped(), historyClamped: true);

        states["glucose"].Should().Be(ReportSignalAvailability.None, "the only reading is three days old");
        states["iob"].Should().Be(ReportSignalAvailability.Data, "the snapshot is two hours old");
    }

    [Fact]
    public async Task The_range_is_cut_to_the_last_24_hours_for_a_clamped_caller_as_a_run_cuts_it_whatever_the_database_shows()
    {
        var states = await StatesAsync(database.Tenant, Everything, reader: null, historyClamped: true);

        states["glucose"].Should().Be(ReportSignalAvailability.None);
        states["iob"].Should().Be(ReportSignalAvailability.Data);
    }

    [Fact]
    public async Task Another_tenant_sees_none_of_the_tenants_rows()
    {
        var states = await StatesAsync(database.Other, Everything);

        states["glucose"].Should().Be(ReportSignalAvailability.None);
        states["iob"].Should().Be(ReportSignalAvailability.None);
        states["basalScheduled"].Should().Be(ReportSignalAvailability.None);
    }
}
