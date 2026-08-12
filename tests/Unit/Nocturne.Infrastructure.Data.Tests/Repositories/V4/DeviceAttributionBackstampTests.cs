using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// Covers the unattributed-backlog read and batch back-stamp on the non-glucose device-attributed
/// types, which a device registration re-attributes alongside sensor glucose. Uses in-memory SQLite
/// so the window, ordering, cap, and patient_device_id writes are exercised end-to-end.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class DeviceAttributionBackstampTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Base = new(2026, 6, 10, 8, 0, 0, DateTimeKind.Utc);

    private readonly DbConnection _connection;
    private readonly NocturneDbContext _context;
    private readonly BolusRepository _boluses;
    private readonly TempBasalRepository _tempBasals;
    private readonly BasalInjectionRepository _basalInjections;
    private readonly MeterGlucoseRepository _meterGlucose;
    private readonly DeviceEventRepository _deviceEvents;

    public DeviceAttributionBackstampTests()
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
        var factory = new TestTenantDbContextFactory(_context);
        var dedup = new Mock<IDeduplicationService>().Object;
        var audit = new Mock<IAuditContext>().Object;

        _boluses = new BolusRepository(factory, dedup, audit, NullLogger<BolusRepository>.Instance);
        _tempBasals = new TempBasalRepository(factory, dedup, audit, NullLogger<TempBasalRepository>.Instance);
        _basalInjections = new BasalInjectionRepository(_context, audit, NullLogger<BasalInjectionRepository>.Instance);
        _meterGlucose = new MeterGlucoseRepository(factory, audit, NullLogger<MeterGlucoseRepository>.Instance);
        _deviceEvents = new DeviceEventRepository(factory, dedup, audit, NullLogger<DeviceEventRepository>.Instance);
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
            DeviceCategory = "InsulinPump",
            Manufacturer = "Tandem",
            Model = "t:slim X2",
            IsCurrent = true,
        });
        _context.SaveChanges();
        return id;
    }

    private Guid SeedBolus(DateTime timestamp, Guid? patientDeviceId = null)
    {
        var id = Guid.NewGuid();
        _context.Boluses.Add(new BolusEntity
        {
            Id = id,
            TenantId = TestTenantId,
            Timestamp = timestamp,
            Insulin = 1.5,
            PatientDeviceId = patientDeviceId,
        });
        _context.SaveChanges();
        return id;
    }

    private Guid SeedTempBasal(DateTime startTimestamp, Guid? patientDeviceId = null)
    {
        var id = Guid.NewGuid();
        _context.TempBasals.Add(new TempBasalEntity
        {
            Id = id,
            TenantId = TestTenantId,
            StartTimestamp = startTimestamp,
            Rate = 0.8,
            Origin = nameof(TempBasalOrigin.Algorithm),
            PatientDeviceId = patientDeviceId,
        });
        _context.SaveChanges();
        return id;
    }

    private Guid SeedBasalInjection(DateTime timestamp, Guid? patientDeviceId = null)
    {
        var id = Guid.NewGuid();
        _context.BasalInjections.Add(new BasalInjectionEntity
        {
            Id = id,
            TenantId = TestTenantId,
            Timestamp = timestamp,
            Units = 22,
            PatientDeviceId = patientDeviceId,
        });
        _context.SaveChanges();
        return id;
    }

    private Guid SeedMeterGlucose(DateTime timestamp, Guid? patientDeviceId = null)
    {
        var id = Guid.NewGuid();
        _context.MeterGlucose.Add(new MeterGlucoseEntity
        {
            Id = id,
            TenantId = TestTenantId,
            Timestamp = timestamp,
            Mgdl = 130,
            PatientDeviceId = patientDeviceId,
        });
        _context.SaveChanges();
        return id;
    }

    private Guid SeedDeviceEvent(DateTime timestamp, DeviceEventType eventType, Guid? patientDeviceId = null)
    {
        var id = Guid.NewGuid();
        _context.DeviceEvents.Add(new DeviceEventEntity
        {
            Id = id,
            TenantId = TestTenantId,
            Timestamp = timestamp,
            EventType = eventType.ToString(),
            PatientDeviceId = patientDeviceId,
        });
        _context.SaveChanges();
        return id;
    }

    [Fact]
    public async Task Boluses_ReturnsUnattributedInWindow_ExcludingAttributedAndOutOfWindow()
    {
        var deviceId = SeedDevice();
        var inWindow = SeedBolus(Base);
        SeedBolus(Base.AddDays(-30));                       // before the window
        SeedBolus(Base.AddHours(1), patientDeviceId: deviceId); // already attributed

        var result = await _boluses.GetUnattributedAsync(Base.AddDays(-1), Base.AddDays(1), limit: 100);

        result.Should().ContainSingle().Which.Id.Should().Be(inWindow);
    }

    [Fact]
    public async Task Boluses_WindowIsInclusiveAtBothBounds()
    {
        var from = Base;
        var to = Base.AddHours(4);
        var atFrom = SeedBolus(from);
        var atTo = SeedBolus(to);
        SeedBolus(from.AddMilliseconds(-1));
        SeedBolus(to.AddMilliseconds(1));

        var result = await _boluses.GetUnattributedAsync(from, to, limit: 100);

        result.Select(b => b.Id).Should().BeEquivalentTo(new[] { atFrom, atTo });
    }

    [Fact]
    public async Task TempBasals_WindowIsInclusiveAtBothBounds()
    {
        var from = Base;
        var to = Base.AddHours(4);
        var atFrom = SeedTempBasal(from);
        var atTo = SeedTempBasal(to);
        SeedTempBasal(from.AddMilliseconds(-1));
        SeedTempBasal(to.AddMilliseconds(1));

        var result = await _tempBasals.GetUnattributedAsync(from, to, limit: 100);

        result.Select(t => t.Id).Should().BeEquivalentTo(new[] { atFrom, atTo });
    }

    [Fact]
    public async Task Boluses_NewestFirst_AtCapReturnsAll_OneOverDropsOldest()
    {
        var oldest = SeedBolus(Base);
        var middle = SeedBolus(Base.AddHours(1));
        var newest = SeedBolus(Base.AddHours(2));

        var atCap = await _boluses.GetUnattributedAsync(from: null, to: null, limit: 3);
        atCap.Select(b => b.Id).Should().Equal(newest, middle, oldest);

        var overCap = await _boluses.GetUnattributedAsync(from: null, to: null, limit: 2);
        overCap.Select(b => b.Id).Should().Equal(newest, middle);
        overCap.Should().NotContain(b => b.Id == oldest);
    }

    [Fact]
    public async Task Boluses_SetPatientDeviceIds_UpdatesOnlyMappedRows()
    {
        var deviceId = SeedDevice();
        var stamped = SeedBolus(Base);
        var untouched = SeedBolus(Base.AddHours(1));

        var updated = await _boluses.SetPatientDeviceIdsAsync(new Dictionary<Guid, Guid> { [stamped] = deviceId });

        updated.Should().Be(1);
        (await _context.Boluses.AsNoTracking().FirstAsync(e => e.Id == stamped)).PatientDeviceId.Should().Be(deviceId);
        (await _context.Boluses.AsNoTracking().FirstAsync(e => e.Id == untouched)).PatientDeviceId.Should().BeNull();
    }

    [Fact]
    public async Task TempBasals_WindowAndOrderOnSpanStart_AtCapReturnsAll_OneOverDropsOldest()
    {
        var deviceId = SeedDevice();
        var oldest = SeedTempBasal(Base);
        var newest = SeedTempBasal(Base.AddHours(2));
        SeedTempBasal(Base.AddDays(-30));                        // before the window
        SeedTempBasal(Base.AddHours(1), patientDeviceId: deviceId); // already attributed

        var atCap = await _tempBasals.GetUnattributedAsync(Base.AddDays(-1), Base.AddDays(1), limit: 2);
        atCap.Select(t => t.Id).Should().Equal(newest, oldest);

        var overCap = await _tempBasals.GetUnattributedAsync(Base.AddDays(-1), Base.AddDays(1), limit: 1);
        overCap.Select(t => t.Id).Should().Equal(newest);
    }

    [Fact]
    public async Task TempBasals_SetPatientDeviceIds_UpdatesOnlyMappedRows()
    {
        var deviceId = SeedDevice();
        var stamped = SeedTempBasal(Base);
        var untouched = SeedTempBasal(Base.AddHours(1));

        var updated = await _tempBasals.SetPatientDeviceIdsAsync(new Dictionary<Guid, Guid> { [stamped] = deviceId });

        updated.Should().Be(1);
        (await _context.TempBasals.AsNoTracking().FirstAsync(e => e.Id == stamped)).PatientDeviceId.Should().Be(deviceId);
        (await _context.TempBasals.AsNoTracking().FirstAsync(e => e.Id == untouched)).PatientDeviceId.Should().BeNull();
    }

    [Fact]
    public async Task BasalInjections_ReturnsUnattributedNewestFirst_AndBackStamps()
    {
        var deviceId = SeedDevice();
        var oldest = SeedBasalInjection(Base);
        var newest = SeedBasalInjection(Base.AddHours(2));
        SeedBasalInjection(Base.AddHours(1), patientDeviceId: deviceId);

        var atCap = await _basalInjections.GetUnattributedAsync(from: null, to: null, limit: 2);
        atCap.Select(b => b.Id).Should().Equal(newest, oldest);

        var overCap = await _basalInjections.GetUnattributedAsync(from: null, to: null, limit: 1);
        overCap.Select(b => b.Id).Should().Equal(newest);

        var updated = await _basalInjections.SetPatientDeviceIdsAsync(new Dictionary<Guid, Guid> { [oldest] = deviceId });
        updated.Should().Be(1);
        (await _context.BasalInjections.AsNoTracking().FirstAsync(e => e.Id == oldest)).PatientDeviceId.Should().Be(deviceId);
    }

    [Fact]
    public async Task MeterGlucose_ReturnsUnattributedNewestFirst_AndBackStamps()
    {
        var deviceId = SeedDevice();
        var oldest = SeedMeterGlucose(Base);
        var newest = SeedMeterGlucose(Base.AddHours(2));
        SeedMeterGlucose(Base.AddHours(1), patientDeviceId: deviceId);

        var atCap = await _meterGlucose.GetUnattributedAsync(from: null, to: null, limit: 2);
        atCap.Select(m => m.Id).Should().Equal(newest, oldest);

        var overCap = await _meterGlucose.GetUnattributedAsync(from: null, to: null, limit: 1);
        overCap.Select(m => m.Id).Should().Equal(newest);

        var updated = await _meterGlucose.SetPatientDeviceIdsAsync(new Dictionary<Guid, Guid> { [newest] = deviceId });
        updated.Should().Be(1);
        (await _context.MeterGlucose.AsNoTracking().FirstAsync(e => e.Id == newest)).PatientDeviceId.Should().Be(deviceId);
    }

    [Fact]
    public async Task DeviceEvents_ReturnsOnlyRequestedEventTypes()
    {
        var sensorStart = SeedDeviceEvent(Base, DeviceEventType.SensorStart);
        var sensorChange = SeedDeviceEvent(Base.AddHours(1), DeviceEventType.SensorChange);
        SeedDeviceEvent(Base.AddHours(2), DeviceEventType.SiteChange);
        SeedDeviceEvent(Base.AddHours(3), DeviceEventType.PodChange);

        var sensorEvents = await _deviceEvents.GetUnattributedAsync(
            from: null, to: null,
            eventTypes: [DeviceEventType.SensorStart, DeviceEventType.SensorChange],
            limit: 100);

        sensorEvents.Select(e => e.Id).Should().BeEquivalentTo([sensorStart, sensorChange]);
        sensorEvents.Should().OnlyContain(e =>
            e.EventType == DeviceEventType.SensorStart || e.EventType == DeviceEventType.SensorChange);
    }

    [Fact]
    public async Task DeviceEvents_NewestFirst_AtCapReturnsAll_OneOverDropsOldest()
    {
        var deviceId = SeedDevice();
        var oldest = SeedDeviceEvent(Base, DeviceEventType.SiteChange);
        var newest = SeedDeviceEvent(Base.AddHours(2), DeviceEventType.SiteChange);
        SeedDeviceEvent(Base.AddHours(1), DeviceEventType.SiteChange, patientDeviceId: deviceId);
        SeedDeviceEvent(Base.AddDays(-30), DeviceEventType.SiteChange);

        var atCap = await _deviceEvents.GetUnattributedAsync(
            Base.AddDays(-1), Base.AddDays(1), [DeviceEventType.SiteChange], limit: 2);
        atCap.Select(e => e.Id).Should().Equal(newest, oldest);

        var overCap = await _deviceEvents.GetUnattributedAsync(
            Base.AddDays(-1), Base.AddDays(1), [DeviceEventType.SiteChange], limit: 1);
        overCap.Select(e => e.Id).Should().Equal(newest);
    }

    [Fact]
    public async Task DeviceEvents_NoEventTypes_ReturnsEmpty()
    {
        SeedDeviceEvent(Base, DeviceEventType.SiteChange);

        var result = await _deviceEvents.GetUnattributedAsync(from: null, to: null, eventTypes: [], limit: 100);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DeviceEvents_SetPatientDeviceIds_UpdatesOnlyMappedRows()
    {
        var deviceId = SeedDevice();
        var stamped = SeedDeviceEvent(Base, DeviceEventType.SiteChange);
        var untouched = SeedDeviceEvent(Base.AddHours(1), DeviceEventType.SiteChange);

        var updated = await _deviceEvents.SetPatientDeviceIdsAsync(new Dictionary<Guid, Guid> { [stamped] = deviceId });

        updated.Should().Be(1);
        (await _context.DeviceEvents.AsNoTracking().FirstAsync(e => e.Id == stamped)).PatientDeviceId.Should().Be(deviceId);
        (await _context.DeviceEvents.AsNoTracking().FirstAsync(e => e.Id == untouched)).PatientDeviceId.Should().BeNull();
    }
}
