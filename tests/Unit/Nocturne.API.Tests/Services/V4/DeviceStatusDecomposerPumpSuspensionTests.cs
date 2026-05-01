using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// Tests for the pump-suspension transition detection inside <see cref="DeviceStatusDecomposer"/>.
/// Verifies <c>false → true</c> opens, <c>true → false</c> closes, equal-state no-ops, first-observation
/// behaviour, idempotency under retry, and pump-clock preference for transition timestamps.
/// </summary>
public class DeviceStatusDecomposerPumpSuspensionTests : IDisposable
{
    private readonly NocturneDbContext _context;
    private readonly Mock<IPumpSnapshotRepository> _pumpRepoMock = new();
    private readonly Mock<IApsSnapshotRepository> _apsRepoMock = new();
    private readonly Mock<IUploaderSnapshotRepository> _uploaderRepoMock = new();
    private readonly Mock<IDeviceStatusExtrasRepository> _extrasRepoMock = new();
    private readonly Mock<IStateSpanService> _stateSpanServiceMock = new();
    private readonly Mock<IDeviceService> _deviceServiceMock = new();
    private readonly DeviceStatusDecomposer _decomposer;

    public DeviceStatusDecomposerPumpSuspensionTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        // Default upsert returns the input span with a generated id (mimics service behaviour).
        _stateSpanServiceMock
            .Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateSpan s, CancellationToken _) =>
            {
                s.Id ??= Guid.NewGuid().ToString();
                return s;
            });

        // Default GetStateSpansAsync returns empty (no open span) unless the test overrides it.
        _stateSpanServiceMock
            .Setup(s => s.GetStateSpansAsync(
                It.IsAny<StateSpanCategory?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<bool?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<StateSpan>());

        // CreateAsync echoes the model with an Id assigned, like a real repo.
        _pumpRepoMock
            .Setup(r => r.CreateAsync(It.IsAny<V4Models.PumpSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((V4Models.PumpSnapshot m, CancellationToken _) =>
            {
                if (m.Id == Guid.Empty) m.Id = Guid.NewGuid();
                return m;
            });
        _pumpRepoMock
            .Setup(r => r.UpdateAsync(It.IsAny<Guid>(), It.IsAny<V4Models.PumpSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, V4Models.PumpSnapshot m, CancellationToken _) =>
            {
                m.Id = id;
                return m;
            });

        _decomposer = new DeviceStatusDecomposer(
            _context,
            _apsRepoMock.Object,
            _pumpRepoMock.Object,
            _uploaderRepoMock.Object,
            _extrasRepoMock.Object,
            _stateSpanServiceMock.Object,
            _deviceServiceMock.Object,
            NullLogger<DeviceStatusDecomposer>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static DeviceStatus MakeDeviceStatus(string id, long mills, bool suspended, string? clockIso = null)
    {
        return new DeviceStatus
        {
            Id = id,
            Mills = mills,
            Device = "openaps://test",
            Pump = new PumpStatus
            {
                Manufacturer = "Insulet",
                Model = "Omnipod",
                Status = new PumpStatusDetails { Suspended = suspended },
                Clock = clockIso,
            },
        };
    }

    [Fact]
    public async Task FalseToTrue_OpensSpan()
    {
        // Arrange
        var prior = new V4Models.PumpSnapshot { Suspended = false, Timestamp = new DateTime(2024, 1, 1, 11, 0, 0, DateTimeKind.Utc) };
        _pumpRepoMock.Setup(r => r.GetLatestBeforeAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(prior);

        var ds = MakeDeviceStatus("legacy1", 1704110400000 /* 2024-01-01T12:00:00Z */, suspended: true);

        // Act
        await _decomposer.DecomposeAsync(ds);

        // Assert
        _stateSpanServiceMock.Verify(s => s.UpsertStateSpanAsync(
            It.Is<StateSpan>(span =>
                span.Category == StateSpanCategory.PumpMode &&
                span.State == PumpModeState.Suspended.ToString() &&
                span.EndTimestamp == null &&
                span.OriginalId!.StartsWith("pump-suspended:")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TrueToFalse_ClosesOpenSpan()
    {
        // Arrange
        var prior = new V4Models.PumpSnapshot { Suspended = true, Timestamp = new DateTime(2024, 1, 1, 11, 0, 0, DateTimeKind.Utc) };
        _pumpRepoMock.Setup(r => r.GetLatestBeforeAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(prior);

        var openSpan = new StateSpan
        {
            Id = "span-1",
            Category = StateSpanCategory.PumpMode,
            State = PumpModeState.Suspended.ToString(),
            StartTimestamp = prior.Timestamp,
            EndTimestamp = null,
        };
        _stateSpanServiceMock
            .Setup(s => s.GetStateSpansAsync(
                StateSpanCategory.PumpMode, PumpModeState.Suspended.ToString(),
                null, null, null, true,
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { openSpan });

        var ds = MakeDeviceStatus("legacy2", 1704110400000, suspended: false);

        // Act
        await _decomposer.DecomposeAsync(ds);

        // Assert: upsert called with EndTimestamp populated on the same span
        _stateSpanServiceMock.Verify(s => s.UpsertStateSpanAsync(
            It.Is<StateSpan>(span =>
                span.Id == "span-1" &&
                span.EndTimestamp != null),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TrueToTrue_NoOp()
    {
        var prior = new V4Models.PumpSnapshot { Suspended = true, Timestamp = new DateTime(2024, 1, 1, 11, 0, 0, DateTimeKind.Utc) };
        _pumpRepoMock.Setup(r => r.GetLatestBeforeAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(prior);

        var ds = MakeDeviceStatus("legacy3", 1704110400000, suspended: true);
        await _decomposer.DecomposeAsync(ds);

        _stateSpanServiceMock.Verify(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FalseToFalse_NoOp()
    {
        var prior = new V4Models.PumpSnapshot { Suspended = false, Timestamp = new DateTime(2024, 1, 1, 11, 0, 0, DateTimeKind.Utc) };
        _pumpRepoMock.Setup(r => r.GetLatestBeforeAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(prior);

        var ds = MakeDeviceStatus("legacy4", 1704110400000, suspended: false);
        await _decomposer.DecomposeAsync(ds);

        _stateSpanServiceMock.Verify(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FirstObservationTrue_OpensSpan()
    {
        // No prior snapshot exists.
        _pumpRepoMock.Setup(r => r.GetLatestBeforeAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((V4Models.PumpSnapshot?)null);

        var ds = MakeDeviceStatus("legacy5", 1704110400000, suspended: true);
        await _decomposer.DecomposeAsync(ds);

        _stateSpanServiceMock.Verify(s => s.UpsertStateSpanAsync(
            It.Is<StateSpan>(span =>
                span.Category == StateSpanCategory.PumpMode &&
                span.State == PumpModeState.Suspended.ToString() &&
                span.EndTimestamp == null),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task FirstObservationFalse_DoesNotOpenSpan()
    {
        _pumpRepoMock.Setup(r => r.GetLatestBeforeAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((V4Models.PumpSnapshot?)null);

        var ds = MakeDeviceStatus("legacy6", 1704110400000, suspended: false);
        await _decomposer.DecomposeAsync(ds);

        _stateSpanServiceMock.Verify(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OutOfOrderUpload_UsesPumpClockWhenPresent()
    {
        // Prior says "not suspended". New record has earlier ingest timestamp but pump clock matches a real wall-clock.
        var prior = new V4Models.PumpSnapshot { Suspended = false, Timestamp = new DateTime(2024, 1, 1, 11, 0, 0, DateTimeKind.Utc) };
        _pumpRepoMock.Setup(r => r.GetLatestBeforeAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(prior);

        var pumpClock = "2024-01-01T11:30:00Z";
        var expectedTransitionAt = DateTime.Parse("2024-01-01T11:30:00Z").ToUniversalTime();

        var ds = MakeDeviceStatus("legacy7", 1704110400000 /* 12:00 ingest */, suspended: true, clockIso: pumpClock);

        await _decomposer.DecomposeAsync(ds);

        _stateSpanServiceMock.Verify(s => s.UpsertStateSpanAsync(
            It.Is<StateSpan>(span =>
                span.StartTimestamp == expectedTransitionAt),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task IdempotentOpen_SameOriginalId()
    {
        // Same legacyId/snapshot.Id processed twice should produce the same OriginalId on the upsert,
        // which the StateSpan upsert layer dedupes on.
        var prior = new V4Models.PumpSnapshot { Suspended = false, Timestamp = new DateTime(2024, 1, 1, 11, 0, 0, DateTimeKind.Utc) };
        _pumpRepoMock.Setup(r => r.GetLatestBeforeAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(prior);

        // Pin the snapshot id on the second call so OriginalId is deterministic across both invocations.
        var pinnedId = Guid.NewGuid();
        _pumpRepoMock
            .Setup(r => r.CreateAsync(It.IsAny<V4Models.PumpSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((V4Models.PumpSnapshot m, CancellationToken _) =>
            {
                m.Id = pinnedId;
                return m;
            });

        var ds = MakeDeviceStatus("legacy8", 1704110400000, suspended: true);

        await _decomposer.DecomposeAsync(ds);
        await _decomposer.DecomposeAsync(ds);

        var expectedOriginalId = $"pump-suspended:{pinnedId}";
        _stateSpanServiceMock.Verify(s => s.UpsertStateSpanAsync(
            It.Is<StateSpan>(span => span.OriginalId == expectedOriginalId),
            It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }
}
