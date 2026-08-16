using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// Covers the optional patient-device scope on
/// <see cref="DeviceEventRepository.GetLatestByEventTypesAsync"/>, which backs the CAGE/SAGE/IAGE/BAGE
/// ages. A wearer with two CGMs or two pumps otherwise gets a blended age: a change on one device
/// resets the age reported for the other.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
[Trait("Category", "DeviceEvent")]
public class DeviceEventRepositoryPatientDeviceScopeTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private readonly DbConnection _connection;
    private readonly NocturneDbContext _context;
    private readonly DeviceEventRepository _repo;

    public DeviceEventRepositoryPatientDeviceScopeTests()
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

        _repo = new DeviceEventRepository(
            new TestTenantDbContextFactory(_context),
            new Mock<IDeduplicationService>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<DeviceEventRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid SeedDevice(string category, string model)
    {
        var id = Guid.NewGuid();
        _context.PatientDevices.Add(new PatientDeviceEntity
        {
            Id = id,
            TenantId = TestTenantId,
            DeviceCategory = category,
            Manufacturer = "Test",
            Model = model,
            IsCurrent = true,
        });
        _context.SaveChanges();
        return id;
    }

    private void SeedEvent(DeviceEventType eventType, DateTime timestamp, Guid? patientDeviceId)
    {
        _context.DeviceEvents.Add(new DeviceEventEntity
        {
            Id = Guid.NewGuid(),
            TenantId = TestTenantId,
            Timestamp = timestamp,
            EventType = eventType.ToString(),
            PatientDeviceId = patientDeviceId,
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task GetLatestByEventTypesAsync_ScopedToAPatientDevice_IgnoresANewerEventOnAnotherDevice()
    {
        var firstSensor = SeedDevice("CGM", "G7");
        var secondSensor = SeedDevice("CGM", "Libre 3");

        var firstChange = new DateTime(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc);
        var secondChange = new DateTime(2026, 8, 5, 6, 0, 0, DateTimeKind.Utc);
        SeedEvent(DeviceEventType.SensorChange, firstChange, firstSensor);
        SeedEvent(DeviceEventType.SensorChange, secondChange, secondSensor);

        var latest = await _repo.GetLatestByEventTypesAsync([DeviceEventType.SensorChange], firstSensor);

        latest.Should().NotBeNull();
        latest!.Timestamp.Should().Be(firstChange);
        latest.PatientDeviceId.Should().Be(firstSensor);
    }

    [Fact]
    public async Task GetLatestByEventTypesAsync_ScopedToAPatientDevice_ExcludesUnlinkedEvents()
    {
        var pump = SeedDevice("InsulinPump", "t:slim");

        var linked = new DateTime(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc);
        SeedEvent(DeviceEventType.SiteChange, linked, pump);
        SeedEvent(DeviceEventType.SiteChange, new DateTime(2026, 8, 5, 6, 0, 0, DateTimeKind.Utc), null);

        var latest = await _repo.GetLatestByEventTypesAsync([DeviceEventType.SiteChange], pump);

        latest.Should().NotBeNull();
        latest!.Timestamp.Should().Be(linked);
    }

    [Fact]
    public async Task GetLatestByEventTypesAsync_WithNoPatientDevice_ReturnsTheTenantWideLatest()
    {
        var firstSensor = SeedDevice("CGM", "G7");
        var secondSensor = SeedDevice("CGM", "Libre 3");

        var secondChange = new DateTime(2026, 8, 5, 6, 0, 0, DateTimeKind.Utc);
        SeedEvent(DeviceEventType.SensorChange, new DateTime(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc), firstSensor);
        SeedEvent(DeviceEventType.SensorChange, secondChange, secondSensor);
        SeedEvent(DeviceEventType.SensorChange, new DateTime(2026, 7, 1, 6, 0, 0, DateTimeKind.Utc), null);

        var latest = await _repo.GetLatestByEventTypesAsync([DeviceEventType.SensorChange]);

        latest.Should().NotBeNull();
        latest!.Timestamp.Should().Be(secondChange);
        latest.PatientDeviceId.Should().Be(secondSensor);
    }

    [Fact]
    public async Task GetLatestByEventTypesAsync_ScopedToADeviceWithNoEvents_ReturnsNull()
    {
        var pump = SeedDevice("InsulinPump", "t:slim");
        var otherPump = SeedDevice("InsulinPump", "Omnipod");

        SeedEvent(DeviceEventType.PumpBatteryChange, new DateTime(2026, 8, 5, 6, 0, 0, DateTimeKind.Utc), otherPump);

        var latest = await _repo.GetLatestByEventTypesAsync([DeviceEventType.PumpBatteryChange], pump);

        latest.Should().BeNull();
    }
}
