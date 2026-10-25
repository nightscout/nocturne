using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Services.SetupHub;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.SetupHub;

/// <summary>
/// On PostgreSQL under the runtime role and Row Level Security, the setup hub adding an infusion-set
/// tracker and a legacy conversion of the site age at once leave one site tracker, and the hub's
/// record names it exactly when the hub made it.
/// </summary>
[Trait("Category", "Integration")]
public class SetupHubConversionPostgresTests(SetupHubConversionPostgresTests.Database database)
    : IClassFixture<SetupHubConversionPostgresTests.Database>
{
    private const string OwnerId = "owner";

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("setup_hub_conversion");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private NocturneDbContext Context(Guid tenant) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString, o => o.EnableRetryOnFailure())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options) { TenantId = tenant };

    private async Task<Guid> SeedTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@id, @slug, 'setup-hub-conversion-test', true, now(), now())
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
        cmd.Parameters.Add(new NpgsqlParameter("@slug", $"hub-{tenant:N}"));
        await cmd.ExecuteNonQueryAsync();
        return tenant;
    }

    [Fact]
    public async Task AHubAddAndAConversionAtOnce_LeaveOneSiteTrackerAndAConsistentHubRecord()
    {
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var tenant = await SeedTenantAsync();
            var factory = Context(tenant).AsFactory();
            var patientDevices = new PatientDeviceRepository(factory, NullLogger<PatientDeviceRepository>.Instance);
            await patientDevices.CreateAsync(new PatientDevice
            {
                DeviceCategory = DeviceCategory.InsulinPump, Manufacturer = "SOOIL", Model = "Dana-i",
                CatalogId = "dana-i", IsCurrent = true,
            }, WriteOrigin.Live);

            var deviceEvents = new Mock<IDeviceEventRepository>();
            deviceEvents.Setup(r => r.GetLatestByEventTypesAsync(
                    It.IsAny<DeviceEventType[]>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeviceEvent
                {
                    Id = Guid.NewGuid(), EventType = DeviceEventType.SiteChange, Timestamp = DateTime.UtcNow.AddHours(-20),
                });
            ConsumableTrackerWriter Writer() => new(
                deviceEvents.Object, Mock.Of<ITrackerAlertRuleSyncService>(), Mock.Of<IDeviceEventReactor>(),
                Mock.Of<ISignalRBroadcastService>(), TimeProvider.System, NullLogger<ConsumableTrackerWriter>.Instance);

            var hub = new DeviceSetupService(
                factory,
                patientDevices,
                new PatientInsulinRepository(factory, NullLogger<PatientInsulinRepository>.Instance),
                Mock.Of<IDeviceReattributionService>(),
                new TrackerRepository(Context(tenant).AsFactory()),
                Mock.Of<ITrackerAlertRuleSyncService>(),
                Mock.Of<ITherapySettingsResolver>(),
                Writer(),
                NullLogger<DeviceSetupService>.Instance);
            var conversion = new LegacyFallbackService(
                new TrackerRepository(Context(tenant).AsFactory()),
                deviceEvents.Object,
                patientDevices,
                Writer(),
                TimeProvider.System,
                NullLogger<LegacyFallbackService>.Instance);

            await Task.WhenAll(
                Task.Run(() => hub.AddTrackerAsync(TrackerOfferKind.InfusionSet, "Dana-i infusion set", OwnerId, CancellationToken.None)),
                Task.Run(() => conversion.CreateTrackersAsync([LegacyConsumableKind.Site], OwnerId)));

            await using var db = Context(tenant);
            var sites = await db.TrackerDefinitions.AsNoTracking()
                .Where(d => d.Category == TrackerCategory.Cannula).ToListAsync();
            var site = sites.Should().ContainSingle().Subject;
            var records = await db.SetupHubAdditions.AsNoTracking()
                .Where(a => a.RecordKind == SetupHubRecordKind.Tracker).Select(a => a.RecordId).ToListAsync();
            if (site.Name == "Dana-i infusion set")
                records.Should().Equal(site.Id);
            else
                records.Should().BeEmpty();
        }
    }
}
