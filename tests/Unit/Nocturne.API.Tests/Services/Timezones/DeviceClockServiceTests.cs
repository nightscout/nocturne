using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Timezones;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Timezones;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Timezones;

[Trait("Category", "Unit")]
public class DeviceClockServiceTests : IDisposable
{
    private const string Connector = "glooko";
    private const string OwnerId = "owner-subject";

    private readonly NocturneDbContext _db;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Mock<IInAppNotificationService> _notifications = new();
    private readonly TimezoneTimelineService _timeline;
    private readonly DeviceClockService _service;

    public DeviceClockServiceTests()
    {
        _db = TestDbContextFactory.CreateInMemoryContext();

        var accessor = new Mock<ITenantAccessor>();
        accessor.SetupGet(a => a.Context)
            .Returns(new TenantContext(_tenantId, "test", "Test", IsActive: true, IsDemo: false));

        var owners = new Mock<ITenantOwnerResolver>();
        owners.Setup(o => o.GetOwnerSubjectIdAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnerId);

        _timeline = new TimezoneTimelineService(
            _db, accessor.Object, NullLogger<TimezoneTimelineService>.Instance);
        _service = new DeviceClockService(
            _db, accessor.Object, _timeline, owners.Object, _notifications.Object,
            NullLogger<DeviceClockService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private static DateTime Utc(int d, int h, int mi = 0) => new(2026, 4, d, h, mi, 0, DateTimeKind.Utc);

    private static DeviceClockObservation Batch(DateTime at, int offset, int samples = 8) => new()
    {
        Connector = Connector,
        Source = DeviceClockObservationSource.UploadBatch,
        ObservedAtUtc = at,
        OffsetMinutes = offset,
        IsEstimate = true,
        SampleCount = samples,
    };

    private static DeviceClockObservation Profile(DateTime at, int offset, string zone) => new()
    {
        Connector = Connector,
        Source = DeviceClockObservationSource.Profile,
        ObservedAtUtc = at,
        OffsetMinutes = offset,
        IsEstimate = true,
        SampleCount = 1,
        DeclaredTimezone = zone,
    };

    // Three agreeing deviant estimates: device at UTC+2 while the timeline says EDT (−4).
    private static List<DeviceClockObservation> DeviantRun() =>
        [Batch(Utc(10, 12), 120), Batch(Utc(10, 18), 120), Batch(Utc(11, 6), 120)];

    private async Task SeedHomeZoneAsync() => await _timeline.EnsureOriginAsync("America/New_York");

    // ── Persistence ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Observations_PersistOnce_ReRecordingIsIdempotent()
    {
        await SeedHomeZoneAsync();

        await _service.RecordObservationsAsync(Connector, DeviantRun(), null, correctionsEnabled: false);
        await _service.RecordObservationsAsync(Connector, DeviantRun(), null, correctionsEnabled: false);

        (await _db.DeviceClockObservations.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task ReObservedBatch_WithMoreSamples_ReplacesTheRow()
    {
        await SeedHomeZoneAsync();
        await _service.RecordObservationsAsync(
            Connector, [Batch(Utc(10, 12), 90, samples: 3)], null, correctionsEnabled: false);

        await _service.RecordObservationsAsync(
            Connector, [Batch(Utc(10, 12), 120, samples: 9)], null, correctionsEnabled: false);

        var row = (await _db.DeviceClockObservations.SingleAsync());
        row.SampleCount.Should().Be(9);
        row.OffsetMinutes.Should().Be(120);
    }

    [Fact]
    public async Task StaleObservations_ArePruned()
    {
        await SeedHomeZoneAsync();
        var ancient = Batch(DateTime.UtcNow.AddDays(-DeviceClockService.RetentionDays - 30), 120);
        await _service.RecordObservationsAsync(Connector, [ancient], null, correctionsEnabled: false);

        await _service.RecordObservationsAsync(
            Connector, [Batch(DateTime.UtcNow, -240)], null, correctionsEnabled: false);

        var remaining = await _db.DeviceClockObservations.ToListAsync();
        remaining.Should().ContainSingle()
            .Which.ObservedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    // ── Derivation and gating ────────────────────────────────────────────────

    [Fact]
    public async Task DeviantRun_DerivesASegment_MeasuredAgainstTheTimelineZone()
    {
        await SeedHomeZoneAsync();

        var segments = await _service.RecordObservationsAsync(
            Connector, DeviantRun(), null, correctionsEnabled: false);

        segments.Should().ContainSingle().Which.OffsetMinutes.Should().Be(120);
    }

    [Fact]
    public async Task WithoutATimeline_TheStaticFallbackIsTheExpectedClock()
    {
        // No timeline entries; the connector's static offset (+2h) matches the device: no deviation.
        var segments = await _service.RecordObservationsAsync(
            Connector, DeviantRun(), expectedFallbackOffsetHours: 2, correctionsEnabled: false);

        segments.Should().BeEmpty();
    }

    [Fact]
    public async Task CorrectionsDisabled_NothingUserVisibleHappens()
    {
        await SeedHomeZoneAsync();

        await _service.RecordObservationsAsync(Connector, DeviantRun(), null, correctionsEnabled: false);

        _notifications.Verify(
            n => n.CreateNotificationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<NotificationCategory?>(), It.IsAny<NotificationUrgency?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<List<NotificationActionDto>?>(), It.IsAny<ResolutionConditions?>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        (await _timeline.GetTimelineAsync()).Should().ContainSingle(); // just the origin
    }

    // ── Notifications ────────────────────────────────────────────────────────

    [Fact]
    public async Task NewlyConfirmedSegment_NotifiesTheOwnerOnce()
    {
        await SeedHomeZoneAsync();

        await _service.RecordObservationsAsync(Connector, DeviantRun(), null, correctionsEnabled: true);
        // Same evidence again: the segment already existed before this call.
        await _service.RecordObservationsAsync(Connector, DeviantRun(), null, correctionsEnabled: true);

        _notifications.Verify(
            n => n.CreateNotificationAsync(
                OwnerId, "connector.deviceClockDeviation", "device_clock_deviation",
                It.IsAny<NotificationCategory?>(), It.IsAny<NotificationUrgency?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<List<NotificationActionDto>?>(), It.IsAny<ResolutionConditions?>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RefiningAnExistingSegment_StaysSilent()
    {
        await SeedHomeZoneAsync();
        await _service.RecordObservationsAsync(Connector, DeviantRun(), null, correctionsEnabled: true);
        _notifications.Invocations.Clear();

        // A fourth agreeing observation extends the run; the deviation was already announced.
        await _service.RecordObservationsAsync(
            Connector, [Batch(Utc(11, 12), 120)], null, correctionsEnabled: true);

        _notifications.Invocations.Should().BeEmpty();
    }

    // ── Declared-zone maintenance (#968) ─────────────────────────────────────

    [Fact]
    public async Task SustainedDeclaredZoneChange_AppendsATimelineEntry()
    {
        await SeedHomeZoneAsync();

        // Sydney is UTC+10 in April; the app updated the profile three times after the move.
        await _service.RecordObservationsAsync(
            Connector,
            [
                Profile(Utc(10, 2), 600, "Australia/Sydney"),
                Profile(Utc(11, 2), 600, "Australia/Sydney"),
                Profile(Utc(12, 2), 600, "Australia/Sydney"),
            ],
            null,
            correctionsEnabled: true);

        var entries = await _timeline.GetTimelineAsync();
        entries.Should().HaveCount(2);
        entries[^1].Timezone.Should().Be("Australia/Sydney");
        // Entered at the earliest sustained assertion, expressed in the new zone's wall clock.
        entries[^1].EffectiveFrom.Should().Be(new DateTime(2026, 4, 10, 12, 0, 0)); // 02:00 UTC + 10h
    }

    [Fact]
    public async Task DeclaredZoneChange_NeedsSustainedAgreement()
    {
        await SeedHomeZoneAsync();

        await _service.RecordObservationsAsync(
            Connector,
            [
                Profile(Utc(10, 2), 600, "Australia/Sydney"),
                Profile(Utc(11, 2), -240, "America/New_York"),
                Profile(Utc(12, 2), 600, "Australia/Sydney"),
            ],
            null,
            correctionsEnabled: true);

        (await _timeline.GetTimelineAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task UnchangedDeclaredZone_AppendsNothing()
    {
        await SeedHomeZoneAsync();

        await _service.RecordObservationsAsync(
            Connector,
            [
                Profile(Utc(10, 2), -240, "America/New_York"),
                Profile(Utc(11, 2), -240, "America/New_York"),
                Profile(Utc(12, 2), -240, "America/New_York"),
            ],
            null,
            correctionsEnabled: true);

        (await _timeline.GetTimelineAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task DeclaredZoneAppend_IsIdempotentAcrossSyncs()
    {
        await SeedHomeZoneAsync();
        var run = new List<DeviceClockObservation>
        {
            Profile(Utc(10, 2), 600, "Australia/Sydney"),
            Profile(Utc(11, 2), 600, "Australia/Sydney"),
            Profile(Utc(12, 2), 600, "Australia/Sydney"),
        };

        await _service.RecordObservationsAsync(Connector, run, null, correctionsEnabled: true);
        await _service.RecordObservationsAsync(Connector, run, null, correctionsEnabled: true);

        (await _timeline.GetTimelineAsync()).Should().HaveCount(2);
    }
}
