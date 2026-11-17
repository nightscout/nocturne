using Nocturne.Core.Contracts.Devices;
using FluentAssertions;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Mocks;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Services.Devices;

/// <summary>
/// Unit tests for DeviceService — resolves device identity from (category, type, serial) tuples
/// </summary>
public class DeviceServiceTests
{
    private readonly Mock<IDeviceRepository> _mockRepository;
    private readonly Mock<IPatientDeviceRepository> _mockPatientDeviceRepository;
    private readonly DeviceService _service;
    private readonly List<(Guid Id, DateTime Seen)> _updates = [];

    public DeviceServiceTests()
    {
        _mockRepository = new Mock<IDeviceRepository>();
        _mockPatientDeviceRepository = new Mock<IPatientDeviceRepository>();
        _service = new DeviceService(_mockRepository.Object, _mockPatientDeviceRepository.Object, MockTenantAccessor.Create().Object, DeviceDateClockDoubles.Utc());
        _mockRepository
            .Setup(r => r.WidenSeenWindowAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback((Guid id, DateTime t, WriteOrigin _, CancellationToken _) => _updates.Add((id, t)))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_NullType_ReturnsNull()
    {
        // Act
        var result = await _service.ResolveAsync(DeviceCategory.InsulinPump, null, "ABC123", 1000);

        // Assert
        result.Should().BeNull();
        _mockRepository.Verify(
            r => r.FindByIdentitiesAsync(It.IsAny<IReadOnlyCollection<(DeviceCategory, string, string)>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_NullSerial_ReturnsNull()
    {
        // Act
        var result = await _service.ResolveAsync(DeviceCategory.InsulinPump, "Omnipod DASH", null, 1000);

        // Assert
        result.Should().BeNull();
        _mockRepository.Verify(
            r => r.FindByIdentitiesAsync(It.IsAny<IReadOnlyCollection<(DeviceCategory, string, string)>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_EmptyType_ReturnsNull()
    {
        // Act
        var result = await _service.ResolveAsync(DeviceCategory.InsulinPump, "", "ABC123", 1000);

        // Assert
        result.Should().BeNull();
        _mockRepository.Verify(
            r => r.FindByIdentitiesAsync(It.IsAny<IReadOnlyCollection<(DeviceCategory, string, string)>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_NewDevice_CreatesAndReturnsId()
    {
        // Arrange
        var category = DeviceCategory.InsulinPump;
        var type = "Omnipod DASH";
        var serial = "ABC123";
        var mills = 1700000000000L;

        _mockRepository
            .Setup(r => r.FindByIdentitiesAsync(DeviceRepositoryMatchers.Naming(category, type, serial), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DeviceRepositoryMatchers.Found((Device?)null));

        _mockRepository
            .Setup(r => r.CreateAsync(It.IsAny<Device>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Device device, WriteOrigin _, CancellationToken _) => device);

        // Act
        var result = await _service.ResolveAsync(category, type, serial, mills);

        // Assert
        result.Should().NotBeNull();
        _mockRepository.Verify(
            r => r.CreateAsync(
                It.Is<Device>(d =>
                    d.Category == category &&
                    d.Type == type &&
                    d.Serial == serial &&
                    d.FirstSeenTimestamp == DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime &&
                    d.LastSeenTimestamp == DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_ExistingDevice_ReturnsExistingId()
    {
        // Arrange
        var category = DeviceCategory.InsulinPump;
        var type = "Medtronic 780G";
        var serial = "XYZ789";
        var existingId = Guid.NewGuid();
        var mills = 1700000000000L;

        var existingDevice = new Device
        {
            Id = existingId,
            Category = category,
            Type = type,
            Serial = serial,
            FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1699000000000L).UtcDateTime,
            LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000L).UtcDateTime
        };

        _mockRepository
            .Setup(r => r.FindByIdentitiesAsync(DeviceRepositoryMatchers.Naming(category, type, serial), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DeviceRepositoryMatchers.Found(existingDevice));

        // Act
        var result = await _service.ResolveAsync(category, type, serial, mills);

        // Assert
        result.Should().Be(existingId);
        _mockRepository.Verify(
            r => r.CreateAsync(It.IsAny<Device>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_ExistingDevice_UpdatesLastSeenMills()
    {
        // Arrange
        var category = DeviceCategory.InsulinPump;
        var type = "Omnipod DASH";
        var serial = "ABC123";
        var existingId = Guid.NewGuid();
        var olderMills = 1699000000000L;
        var newerMills = 1700000000000L;

        var existingDevice = new Device
        {
            Id = existingId,
            Category = category,
            Type = type,
            Serial = serial,
            FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1698000000000L).UtcDateTime,
            LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(olderMills).UtcDateTime
        };

        _mockRepository
            .Setup(r => r.FindByIdentitiesAsync(DeviceRepositoryMatchers.Naming(category, type, serial), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DeviceRepositoryMatchers.Found(existingDevice));

        // Act
        var result = await _service.ResolveAsync(category, type, serial, newerMills);

        // Assert
        result.Should().Be(existingId);
        _mockRepository.Verify(
            r => r.WidenSeenWindowAsync(
                existingId,
                DateTimeOffset.FromUnixTimeMilliseconds(newerMills).UtcDateTime,
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_ExistingDevice_DoesNotUpdateWhenMillsOlder()
    {
        // Arrange
        var category = DeviceCategory.InsulinPump;
        var type = "Omnipod DASH";
        var serial = "ABC123";
        var existingId = Guid.NewGuid();
        var newerMills = 1700000000000L;
        var olderMills = 1699000000000L;

        var existingDevice = new Device
        {
            Id = existingId,
            Category = category,
            Type = type,
            Serial = serial,
            FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1698000000000L).UtcDateTime,
            LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(newerMills).UtcDateTime
        };

        _mockRepository
            .Setup(r => r.FindByIdentitiesAsync(DeviceRepositoryMatchers.Naming(category, type, serial), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DeviceRepositoryMatchers.Found(existingDevice));

        // Act
        var result = await _service.ResolveAsync(category, type, serial, olderMills);

        // Assert
        result.Should().Be(existingId);
        _mockRepository.Verify(
            r => r.WidenSeenWindowAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_SameDeviceTwice_UsesCacheSecondTime()
    {
        // Arrange
        var category = DeviceCategory.InsulinPump;
        var type = "Omnipod DASH";
        var serial = "ABC123";
        var existingId = Guid.NewGuid();
        var mills = 1700000000000L;

        var existingDevice = new Device
        {
            Id = existingId,
            Category = category,
            Type = type,
            Serial = serial,
            FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1699000000000L).UtcDateTime,
            LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000L).UtcDateTime
        };

        _mockRepository
            .Setup(r => r.FindByIdentitiesAsync(DeviceRepositoryMatchers.Naming(category, type, serial), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DeviceRepositoryMatchers.Found(existingDevice));

        // Act — call twice with same category/type/serial
        var result1 = await _service.ResolveAsync(category, type, serial, mills);
        var result2 = await _service.ResolveAsync(category, type, serial, mills);

        // Assert
        result1.Should().Be(existingId);
        result2.Should().Be(existingId);
        _mockRepository.Verify(
            r => r.FindByIdentitiesAsync(DeviceRepositoryMatchers.Naming(category, type, serial), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_OldestFirstSequence_AdvancesLastSeenOnEachLaterTime()
    {
        var existingId = Guid.NewGuid();
        SetupExistingDevice(existingId, lastSeenMills: 1_000L);

        await ResolveOmnipodAsync(2_000L);
        await ResolveOmnipodAsync(3_000L);
        await ResolveOmnipodAsync(4_000L);

        VerifyWidenings(existingId, 2_000L, 3_000L, 4_000L);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_NewestFirstSequence_LeavesLastSeenAtNewest()
    {
        var existingId = Guid.NewGuid();
        SetupExistingDevice(existingId, lastSeenMills: 1_000L);

        await ResolveOmnipodAsync(4_000L);
        await ResolveOmnipodAsync(3_000L);
        await ResolveOmnipodAsync(2_000L);

        VerifyWidenings(existingId, 4_000L);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_CacheHitWithEarlierOrEqualTime_IssuesNoUpdate()
    {
        var existingId = Guid.NewGuid();
        SetupExistingDevice(existingId, lastSeenMills: 5_000L);

        await ResolveOmnipodAsync(5_000L);
        await ResolveOmnipodAsync(4_000L);
        await ResolveOmnipodAsync(5_000L);

        VerifyWidenings(existingId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_CreatedDeviceThenEarlierAndLaterTimes_WidensBothBounds()
    {
        SetupDeviceCreation();

        var createdId = await ResolveOmnipodAsync(2_000L);
        await ResolveOmnipodAsync(1_000L);
        await ResolveOmnipodAsync(3_000L);

        VerifyWidenings(createdId!.Value, 1_000L, 3_000L);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_NewestFirstBackfillOfNewDevice_MovesFirstSeenToEachOlderTime()
    {
        SetupDeviceCreation();

        var createdId = await ResolveOmnipodAsync(4_000L);
        await ResolveOmnipodAsync(3_000L);
        await ResolveOmnipodAsync(3_500L);
        await ResolveOmnipodAsync(1_000L);

        VerifyWidenings(createdId!.Value, 3_000L, 1_000L);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_ExistingDeviceMeetsTimeBeforeFirstSeen_WidensFirstSeen()
    {
        var existingId = Guid.NewGuid();
        SetupExistingDevice(existingId, lastSeenMills: 5_000L, firstSeenMills: 4_000L);

        await ResolveOmnipodAsync(2_000L);

        VerifyWidenings(existingId, 2_000L);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveManyAsync_SightingsAllAfterTheStoredWindow_WritesOnlyTheLastSeen()
    {
        var existingId = Guid.NewGuid();
        SetupExistingDevice(existingId, lastSeenMills: 1_000L, firstSeenMills: 1_000L);

        await ResolveOmnipodsAsync(2_000L, 6_000L, 4_000L);

        VerifyWidenings(existingId, 6_000L);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveManyAsync_SightingsEitherSideOfTheStoredWindow_WritesEachBoundOnce()
    {
        var existingId = Guid.NewGuid();
        SetupExistingDevice(existingId, lastSeenMills: 5_000L, firstSeenMills: 4_000L);

        await ResolveOmnipodsAsync(3_000L, 6_000L, 2_000L, 7_000L);

        VerifyWidenings(existingId, 7_000L, 2_000L);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveManyAsync_SightingsInsideTheStoredWindow_WritesNothing()
    {
        var existingId = Guid.NewGuid();
        SetupExistingDevice(existingId, lastSeenMills: 9_000L, firstSeenMills: 1_000L);

        await ResolveOmnipodsAsync(2_000L, 5_000L, 9_000L);

        VerifyWidenings(existingId);
    }

    private Task<IReadOnlyList<Guid?>> ResolveOmnipodsAsync(params long[] mills) =>
        _service.ResolveManyAsync([.. mills.Select(m => new DeviceSighting(DeviceCategory.InsulinPump, "Omnipod DASH", "ABC123", m))]);

    private void SetupDeviceCreation()
    {
        _mockRepository
            .Setup(r => r.FindByIdentitiesAsync(DeviceRepositoryMatchers.Naming(DeviceCategory.InsulinPump, "Omnipod DASH", "ABC123"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DeviceRepositoryMatchers.Found((Device?)null));
        _mockRepository
            .Setup(r => r.CreateAsync(It.IsAny<Device>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Device device, WriteOrigin _, CancellationToken _) => device);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_FailedUpdate_LeavesCachedLastSeenAtPersistedValue()
    {
        var existingId = Guid.NewGuid();
        SetupExistingDevice(existingId, lastSeenMills: 1_000L);
        await ResolveOmnipodAsync(1_000L);

        _mockRepository
            .Setup(r => r.WidenSeenWindowAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient"));
        var failed = () => ResolveOmnipodAsync(3_000L);
        await failed.Should().ThrowAsync<InvalidOperationException>();

        _mockRepository
            .Setup(r => r.WidenSeenWindowAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback((Guid id, DateTime t, WriteOrigin _, CancellationToken _) => _updates.Add((id, t)))
            .Returns(Task.CompletedTask);
        await ResolveOmnipodAsync(2_000L);

        VerifyWidenings(existingId, 2_000L);
    }

    private Task<Guid?> ResolveOmnipodAsync(long mills) =>
        _service.ResolveAsync(DeviceCategory.InsulinPump, "Omnipod DASH", "ABC123", mills);

    private void SetupExistingDevice(Guid id, long lastSeenMills, long firstSeenMills = 0L)
    {
        _mockRepository
            .Setup(r => r.FindByIdentitiesAsync(DeviceRepositoryMatchers.Naming(DeviceCategory.InsulinPump, "Omnipod DASH", "ABC123"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DeviceRepositoryMatchers.Found(new Device
            {
                Id = id,
                Category = DeviceCategory.InsulinPump,
                Type = "Omnipod DASH",
                Serial = "ABC123",
                FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(firstSeenMills).UtcDateTime,
                LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(lastSeenMills).UtcDateTime
            }));
    }

    private void VerifyWidenings(Guid id, params long[] expectedMills)
    {
        _updates.Should().Equal(expectedMills.Select(m => (id, DateTimeOffset.FromUnixTimeMilliseconds(m).UtcDateTime)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PatientDeviceLinks_WithMatchingDeviceAndTimestamp_ReturnsPatientDeviceId()
    {
        var deviceId = Guid.CreateVersion7();
        var patientDeviceId = Guid.CreateVersion7();
        var mills = DateTimeOffset.Parse("2026-03-15T12:00:00Z").ToUnixTimeMilliseconds();

        _mockPatientDeviceRepository
            .Setup(r => r.GetByDeviceIdsAsync(DeviceRepositoryMatchers.Including(deviceId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PatientDevice>
            {
                new()
                {
                    Id = patientDeviceId,
                    DeviceId = deviceId,
                    StartDate = new DateOnly(2026, 1, 1),
                    EndDate = new DateOnly(2026, 6, 1),
                }
            });

        var result = (await _service.GetPatientDeviceLinksAsync([deviceId])).Resolve(deviceId, mills);
        result.Should().Be(patientDeviceId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PatientDeviceLinks_WithNullDeviceId_ReturnsNull()
    {
        var result = (await _service.GetPatientDeviceLinksAsync([null])).Resolve(null, 1700000000000L);
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PatientDeviceLinks_WithNoMatchingPatientDevice_ReturnsNull()
    {
        var deviceId = Guid.CreateVersion7();
        var mills = DateTimeOffset.Parse("2026-03-15T12:00:00Z").ToUnixTimeMilliseconds();

        _mockPatientDeviceRepository
            .Setup(r => r.GetByDeviceIdsAsync(DeviceRepositoryMatchers.Including(deviceId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PatientDevice>());

        var result = (await _service.GetPatientDeviceLinksAsync([deviceId])).Resolve(deviceId, mills);
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PatientDeviceLinks_WithTimestampOutsideDateRange_ReturnsNull()
    {
        var deviceId = Guid.CreateVersion7();
        var patientDeviceId = Guid.CreateVersion7();
        var mills = DateTimeOffset.Parse("2025-06-15T12:00:00Z").ToUnixTimeMilliseconds();

        _mockPatientDeviceRepository
            .Setup(r => r.GetByDeviceIdsAsync(DeviceRepositoryMatchers.Including(deviceId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PatientDevice>
            {
                new()
                {
                    Id = patientDeviceId,
                    DeviceId = deviceId,
                    StartDate = new DateOnly(2026, 1, 1),
                    EndDate = new DateOnly(2026, 6, 1),
                }
            });

        var result = (await _service.GetPatientDeviceLinksAsync([deviceId])).Resolve(deviceId, mills);
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PatientDeviceLinks_WithNullEndDate_MatchesCurrentDevice()
    {
        var deviceId = Guid.CreateVersion7();
        var patientDeviceId = Guid.CreateVersion7();
        var mills = DateTimeOffset.Parse("2030-01-01T12:00:00Z").ToUnixTimeMilliseconds();

        _mockPatientDeviceRepository
            .Setup(r => r.GetByDeviceIdsAsync(DeviceRepositoryMatchers.Including(deviceId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PatientDevice>
            {
                new()
                {
                    Id = patientDeviceId,
                    DeviceId = deviceId,
                    StartDate = new DateOnly(2026, 1, 1),
                    EndDate = null,
                }
            });

        var result = (await _service.GetPatientDeviceLinksAsync([deviceId])).Resolve(deviceId, mills);
        result.Should().Be(patientDeviceId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PatientDeviceLinks_SameDeviceTwice_UsesCacheSecondTime()
    {
        var deviceId = Guid.CreateVersion7();
        var patientDeviceId = Guid.CreateVersion7();
        var mills = DateTimeOffset.Parse("2026-03-15T12:00:00Z").ToUnixTimeMilliseconds();

        _mockPatientDeviceRepository
            .Setup(r => r.GetByDeviceIdsAsync(DeviceRepositoryMatchers.Including(deviceId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PatientDevice>
            {
                new()
                {
                    Id = patientDeviceId,
                    DeviceId = deviceId,
                    StartDate = new DateOnly(2026, 1, 1),
                    EndDate = null,
                }
            });

        var result1 = (await _service.GetPatientDeviceLinksAsync([deviceId])).Resolve(deviceId, mills);
        var result2 = (await _service.GetPatientDeviceLinksAsync([deviceId])).Resolve(deviceId, mills);

        result1.Should().Be(patientDeviceId);
        result2.Should().Be(patientDeviceId);
        _mockPatientDeviceRepository.Verify(
            r => r.GetByDeviceIdsAsync(DeviceRepositoryMatchers.Including(deviceId), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
