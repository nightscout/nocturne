using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// Covers the device-attribution surface added to <see cref="SensorGlucoseRepository"/> for the
/// multi-CGM feature: the <c>patientDeviceId</c> read filter, discovered-source aggregation,
/// unattributed windowing, and batch back-stamping. Uses in-memory SQLite so the GroupBy in
/// <c>GetDiscoveredSourcesAsync</c> and the patient_device_id foreign key are enforced end-to-end.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
[Trait("Category", "SensorGlucose")]
public class SensorGlucoseRepositoryAttributionTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly DbConnection _connection;
    private readonly NocturneDbContext _context;
    private readonly SensorGlucoseRepository _repo;

    public SensorGlucoseRepositoryAttributionTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        using (var seedContext = new NocturneDbContext(options))
        {
            seedContext.TenantId = TestTenantId;
            seedContext.Database.EnsureCreated();
            seedContext.Tenants.Add(new TenantEntity { Id = TestTenantId, Slug = "test" });
            seedContext.SaveChanges();
        }

        _context = new NocturneDbContext(options) { TenantId = TestTenantId };

        var dedup = new Mock<IDeduplicationService>();
        _repo = new SensorGlucoseRepository(
            new TestTenantDbContextFactory(_context),
            dedup.Object,
            new Mock<IAuditContext>().Object,
            NullLogger<SensorGlucoseRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid SeedDevice()
    {
        var id = Guid.NewGuid();
        _context.PatientDevices.Add(new PatientDeviceEntity
        {
            Id = id,
            TenantId = TestTenantId,
            DeviceCategory = "CGM",
            Manufacturer = "Dexcom",
            Model = "G7",
            IsCurrent = true,
        });
        _context.SaveChanges();
        return id;
    }

    private Guid SeedReading(
        DateTime timestamp,
        string? dataSource = null,
        string? device = null,
        Guid? patientDeviceId = null)
    {
        var id = Guid.NewGuid();
        _context.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = id,
            TenantId = TestTenantId,
            Timestamp = timestamp,
            Mgdl = 120,
            DataSource = dataSource,
            Device = device,
            PatientDeviceId = patientDeviceId,
        });
        _context.SaveChanges();
        return id;
    }

    [Fact]
    public async Task GetAsync_WithPatientDeviceId_ReturnsOnlyThatDevicesReadings()
    {
        var deviceId = SeedDevice();
        var now = DateTime.UtcNow;
        var attributedId = SeedReading(now, dataSource: "dexcom", patientDeviceId: deviceId);
        SeedReading(now.AddMinutes(-5), dataSource: "dexcom"); // unattributed

        var result = (await _repo.GetAsync(
            from: null, to: null, device: null, source: null,
            limit: 100, offset: 0, descending: true,
            nativeOnly: false, afterTimestamp: null, afterId: null,
            patientDeviceId: deviceId)).ToList();

        result.Should().ContainSingle();
        result[0].Id.Should().Be(attributedId);
    }

    [Fact]
    public async Task GetAsync_WithoutPatientDeviceId_ReturnsAllReadings()
    {
        var deviceId = SeedDevice();
        var now = DateTime.UtcNow;
        SeedReading(now, dataSource: "dexcom", patientDeviceId: deviceId);
        SeedReading(now.AddMinutes(-5), dataSource: "dexcom");

        var result = (await _repo.GetAsync(from: null, to: null, device: null, source: null)).ToList();

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetDiscoveredSourcesAsync_GroupsUnattributedBySourceAndDevice_ExcludingAttributed()
    {
        var deviceId = SeedDevice();
        var since = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        // Two unattributed readings from the same (source, device) → one group of 2.
        SeedReading(new DateTime(2026, 6, 10, 8, 0, 0, DateTimeKind.Utc), dataSource: "xdrip", device: "DexcomG6");
        var latestXdrip = new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc);
        SeedReading(latestXdrip, dataSource: "xdrip", device: "DexcomG6");
        // A distinct unattributed stream.
        SeedReading(new DateTime(2026, 6, 12, 8, 0, 0, DateTimeKind.Utc), dataSource: "libre", device: "Libre3");
        // Attributed reading — must be excluded from discovery.
        SeedReading(new DateTime(2026, 6, 13, 8, 0, 0, DateTimeKind.Utc), dataSource: "already", device: "Owned", patientDeviceId: deviceId);
        // Older than the window — excluded.
        SeedReading(new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc), dataSource: "old", device: "Old");

        var sources = await _repo.GetDiscoveredSourcesAsync(since);

        sources.Should().HaveCount(2);
        // Ordered by lastSeen desc — libre (12th) before xdrip (11th).
        sources[0].DataSource.Should().Be("libre");
        sources[1].DataSource.Should().Be("xdrip");

        var xdrip = sources.Single(s => s.DataSource == "xdrip");
        xdrip.Device.Should().Be("DexcomG6");
        xdrip.ReadingCount.Should().Be(2);
        xdrip.LastSeen.Should().Be(latestXdrip);

        sources.Should().NotContain(s => s.DataSource == "already");
        sources.Should().NotContain(s => s.DataSource == "old");
    }

    [Fact]
    public async Task GetUnattributedAsync_ReturnsUnattributedInWindow_NewestFirst_CappedAtLimit()
    {
        var deviceId = SeedDevice();
        var t1 = new DateTime(2026, 6, 10, 8, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 6, 12, 8, 0, 0, DateTimeKind.Utc);
        SeedReading(t1, dataSource: "a");
        SeedReading(t2, dataSource: "b");
        SeedReading(t3, dataSource: "c");
        SeedReading(t3.AddHours(1), dataSource: "attributed", patientDeviceId: deviceId);

        var result = await _repo.GetUnattributedAsync(
            from: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            to: new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            limit: 2);

        result.Should().HaveCount(2);
        result[0].Timestamp.Should().Be(t3, "results are newest first");
        result[1].Timestamp.Should().Be(t2);
        result.Should().NotContain(r => r.DataSource == "attributed");
    }

    [Fact]
    public async Task SetPatientDeviceIdsAsync_UpdatesOnlyMappedRows_AndReturnsRowCount()
    {
        var deviceId = SeedDevice();
        var now = DateTime.UtcNow;
        var a = SeedReading(now, dataSource: "x");
        var b = SeedReading(now.AddMinutes(-5), dataSource: "x");
        var untouched = SeedReading(now.AddMinutes(-10), dataSource: "x");

        var map = new Dictionary<Guid, Guid>
        {
            [a] = deviceId,
            [b] = deviceId,
            [Guid.NewGuid()] = deviceId, // id not present → ignored
        };

        var updated = await _repo.SetPatientDeviceIdsAsync(map);

        updated.Should().Be(2);

        using var verify = new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite(_connection).Options) { TenantId = TestTenantId };
        (await verify.SensorGlucose.FindAsync(a))!.PatientDeviceId.Should().Be(deviceId);
        (await verify.SensorGlucose.FindAsync(b))!.PatientDeviceId.Should().Be(deviceId);
        (await verify.SensorGlucose.FindAsync(untouched))!.PatientDeviceId.Should().BeNull();
    }

    [Fact]
    public async Task SetPatientDeviceIdsAsync_EmptyMap_ReturnsZero_NoChanges()
    {
        var updated = await _repo.SetPatientDeviceIdsAsync(new Dictionary<Guid, Guid>());
        updated.Should().Be(0);
    }
}
