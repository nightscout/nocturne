using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// The device event list filters, and the count that must match them: a page and its total read the
/// same rows.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
[Trait("Category", "DeviceEvent")]
public class DeviceEventRepositoryListFilterTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000004");
    private static readonly DateTime T0 = new(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc);
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly DeviceEventRepository _repo;

    public DeviceEventRepositoryListFilterTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TestTenantId);
        _context = _db.CreateContext();
        _repo = new DeviceEventRepository(
            new TestNocturneDbContextFactory(_context),
            new Mock<IDuplicateLinker>().Object,
            new Mock<IAuditContext>().Object,
            NullLogger<DeviceEventRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid SeedPatientDevice()
    {
        var id = Guid.NewGuid();
        _context.PatientDevices.Add(new PatientDeviceEntity
        {
            Id = id,
            TenantId = TestTenantId,
            DeviceCategory = "InsulinPump",
            Manufacturer = "Test",
            Model = "Pump",
            IsCurrent = true,
        });
        _context.SaveChanges();
        return id;
    }

    private void SeedEvent(
        DeviceEventType eventType, int minutes, string? device = null, string? source = null, Guid? patientDeviceId = null)
    {
        _context.DeviceEvents.Add(new DeviceEventEntity
        {
            Id = Guid.NewGuid(),
            TenantId = TestTenantId,
            Timestamp = T0.AddMinutes(minutes),
            EventType = eventType.ToString(),
            Device = device,
            DataSource = source,
            PatientDeviceId = patientDeviceId,
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task GetAsync_FilteredToAnEventType_ReturnsTheNewestOfThatTypeNotTheNewestEvent()
    {
        SeedEvent(DeviceEventType.SiteChange, 0);
        SeedEvent(DeviceEventType.SiteChange, 10);
        SeedEvent(DeviceEventType.SensorChange, 20);
        SeedEvent(DeviceEventType.PodChange, 30);

        var page = (await _repo.GetAsync(
            null, null, null, null, limit: 1, eventTypes: [DeviceEventType.SiteChange])).ToList();

        page.Should().ContainSingle().Which.Timestamp.Should().Be(T0.AddMinutes(10));
    }

    [Fact]
    public async Task GetAsync_FilteredToSeveralEventTypes_ReturnsEventsOfAnyOfThem()
    {
        SeedEvent(DeviceEventType.SiteChange, 0);
        SeedEvent(DeviceEventType.SensorChange, 10);
        SeedEvent(DeviceEventType.PodChange, 20);

        var page = await _repo.GetAsync(
            null, null, null, null, eventTypes: [DeviceEventType.SiteChange, DeviceEventType.PodChange]);

        page.Select(e => e.EventType).Should().BeEquivalentTo([DeviceEventType.SiteChange, DeviceEventType.PodChange]);
    }

    [Fact]
    public async Task CountAsync_CountsOnlyTheEventsTheFiltersMatch()
    {
        var pump = SeedPatientDevice();
        SeedEvent(DeviceEventType.SiteChange, 0, device: "pump-a", source: "tandem", patientDeviceId: pump);
        SeedEvent(DeviceEventType.SiteChange, 10, device: "pump-a", source: "tandem", patientDeviceId: pump);
        SeedEvent(DeviceEventType.SensorChange, 20, device: "pump-a", source: "tandem", patientDeviceId: pump);
        SeedEvent(DeviceEventType.SiteChange, 30, device: "pump-b", source: "tandem");
        SeedEvent(DeviceEventType.SiteChange, 40, device: "pump-a", source: "careportal");

        await AssertCountMatchesPage(device: "pump-a", expected: 4);
        await AssertCountMatchesPage(source: "tandem", expected: 4);
        await AssertCountMatchesPage(patientDeviceId: pump, expected: 3);
        await AssertCountMatchesPage(eventTypes: [DeviceEventType.SiteChange], expected: 4);
        await AssertCountMatchesPage(
            device: "pump-a", source: "tandem", patientDeviceId: pump, eventTypes: [DeviceEventType.SiteChange], expected: 2);
        await AssertCountMatchesPage(expected: 5);
    }

    private async Task AssertCountMatchesPage(
        int expected,
        string? device = null,
        string? source = null,
        Guid? patientDeviceId = null,
        IReadOnlyCollection<DeviceEventType>? eventTypes = null)
    {
        var page = await _repo.GetAsync(
            null, null, device, source, limit: 100, patientDeviceId: patientDeviceId, eventTypes: eventTypes);
        var count = await _repo.CountAsync(null, null, device, source, patientDeviceId, eventTypes);

        count.Should().Be(page.Count()).And.Be(expected);
    }
}
