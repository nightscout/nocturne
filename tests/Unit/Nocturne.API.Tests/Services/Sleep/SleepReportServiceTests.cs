using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Sleep;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Sleep;

[Trait("Category", "Unit")]
public class SleepReportServiceTests
{
    private readonly Mock<ISleepSessionRepository> _sessionRepo = new();
    private readonly Mock<ISensorGlucoseRepository> _glucoseRepo = new();
    private readonly Mock<ITherapySettingsResolver> _therapySettings = new();
    private readonly Mock<ITargetRangeResolver> _targetRange = new();
    private readonly Mock<IPatientRecordRepository> _patientRecord = new();
    private readonly SleepReportService _sut;

    public SleepReportServiceTests()
    {
        // No patient record by default → reference ranges fall back to adult-female norms.
        _patientRecord
            .Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientRecord?)null);

        _sut = new SleepReportService(
            _sessionRepo.Object,
            _glucoseRepo.Object,
            _therapySettings.Object,
            _targetRange.Object,
            _patientRecord.Object,
            NullLogger<SleepReportService>.Instance);
    }

    // ── GetSingleNightReportAsync ──────────────────────────────────────────

    [Fact]
    public async Task GetSingleNightReportAsync_ReturnsNull_WhenSessionNotFound()
    {
        _sessionRepo
            .Setup(r => r.GetSessionByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SleepSession?)null);

        var result = await _sut.GetSingleNightReportAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetSingleNightReportAsync_ReturnsReport_WithSessionAndBreakdown()
    {
        var session = new SleepSession
        {
            Id          = Guid.NewGuid().ToString(),
            StartTime   = new DateTime(2026, 1, 15, 22, 0, 0, DateTimeKind.Utc),
            EndTime     = new DateTime(2026, 1, 16, 6, 0, 0, DateTimeKind.Utc),
            DeepSleepMs  = 90L  * 60_000,
            RemSleepMs   = 120L * 60_000,
            LightSleepMs = 150L * 60_000,
            TotalAwakeMs = 20L  * 60_000,
            TotalSleepMs = 360L * 60_000,
            Source       = SleepSource.Oura,
        };

        _sessionRepo
            .Setup(r => r.GetSessionByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        _glucoseRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, false, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());

        var result = await _sut.GetSingleNightReportAsync(Guid.NewGuid());

        result.Should().NotBeNull();
        result!.Session.Should().BeSameAs(session);
        result.StageBreakdown.DeepMinutes.Should().Be(90);
        result.OvernightTir.Should().BeNull();
        result.DawnPhenomenon.Should().BeNull();
    }

    [Fact]
    public async Task GetSingleNightReportAsync_QueriesGlucoseWithSessionWindow()
    {
        var start = new DateTime(2026, 1, 15, 22, 0, 0, DateTimeKind.Utc);
        var end   = new DateTime(2026, 1, 16, 6, 0, 0, DateTimeKind.Utc);

        var session = new SleepSession
        {
            Id          = Guid.NewGuid().ToString(),
            StartTime   = start,
            EndTime     = end,
            TotalSleepMs = 480L * 60_000,
            Source       = SleepSource.Oura,
        };

        _sessionRepo
            .Setup(r => r.GetSessionByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        _glucoseRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, false, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());

        await _sut.GetSingleNightReportAsync(Guid.NewGuid());

        _glucoseRepo.Verify(r => r.GetAsync(
            start, end, null, null,
            int.MaxValue, 0, false, false, null, null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── GetSingleNightReportByDateAsync ────────────────────────────────────

    private void SetupSessionsForByDate(params SleepSession[] sessions)
    {
        _sessionRepo
            .Setup(r => r.GetSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(sessions);
        _glucoseRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, false, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());
    }

    [Fact]
    public async Task GetSingleNightReportByDateAsync_ResolvesNightStartedThatEvening()
    {
        var session = new SleepSession
        {
            Id           = Guid.NewGuid().ToString(),
            StartTime    = new DateTime(2026, 1, 15, 22, 0, 0, DateTimeKind.Utc),
            EndTime      = new DateTime(2026, 1, 16, 6, 0, 0, DateTimeKind.Utc),
            TotalSleepMs = 480L * 60_000,
            Source       = SleepSource.Oura,
        };
        SetupSessionsForByDate(session);

        var result = await _sut.GetSingleNightReportByDateAsync(new DateOnly(2026, 1, 15));

        result.Should().NotBeNull();
        result!.Session.Should().BeSameAs(session);
    }

    [Fact]
    public async Task GetSingleNightReportByDateAsync_ResolvesEarlyMorningNight_ToPreviousDay()
    {
        // In bed 1am Jan 16 → noon rule buckets it to the Jan 15 display night.
        var session = new SleepSession
        {
            Id           = Guid.NewGuid().ToString(),
            StartTime    = new DateTime(2026, 1, 16, 1, 0, 0, DateTimeKind.Utc),
            EndTime      = new DateTime(2026, 1, 16, 8, 0, 0, DateTimeKind.Utc),
            TotalSleepMs = 420L * 60_000,
            Source       = SleepSource.Apple,
        };
        SetupSessionsForByDate(session);

        var result = await _sut.GetSingleNightReportByDateAsync(new DateOnly(2026, 1, 15));

        result.Should().NotBeNull();
        result!.Session.Should().BeSameAs(session);
    }

    [Fact]
    public async Task GetSingleNightReportByDateAsync_ReturnsNull_WhenNoSessionOnDate()
    {
        var session = new SleepSession
        {
            Id           = Guid.NewGuid().ToString(),
            StartTime    = new DateTime(2026, 1, 15, 22, 0, 0, DateTimeKind.Utc),
            EndTime      = new DateTime(2026, 1, 16, 6, 0, 0, DateTimeKind.Utc),
            TotalSleepMs = 480L * 60_000,
            Source       = SleepSource.Oura,
        };
        SetupSessionsForByDate(session);

        var result = await _sut.GetSingleNightReportByDateAsync(new DateOnly(2026, 1, 17));

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetSingleNightReportByDateAsync_DeduplicatesToLongestSession()
    {
        var longer = new SleepSession
        {
            Id           = Guid.NewGuid().ToString(),
            StartTime    = new DateTime(2026, 1, 15, 22, 0, 0, DateTimeKind.Utc),
            EndTime      = new DateTime(2026, 1, 16, 6, 0, 0, DateTimeKind.Utc),
            TotalSleepMs = 460L * 60_000,
            Source       = SleepSource.Garmin,
        };
        var shorter = new SleepSession
        {
            Id           = Guid.NewGuid().ToString(),
            StartTime    = new DateTime(2026, 1, 15, 23, 0, 0, DateTimeKind.Utc),
            EndTime      = new DateTime(2026, 1, 16, 6, 0, 0, DateTimeKind.Utc),
            TotalSleepMs = 360L * 60_000,
            Source       = SleepSource.Apple,
        };
        SetupSessionsForByDate(shorter, longer);

        var result = await _sut.GetSingleNightReportByDateAsync(new DateOnly(2026, 1, 15));

        result.Should().NotBeNull();
        result!.Session.Should().BeSameAs(longer);
    }

    // ── GetTrendsReportAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetTrendsReportAsync_ReturnsEmptyReport_WhenNoSessions()
    {
        _sessionRepo
            .Setup(r => r.GetSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SleepSession>());

        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);

        var result = await _sut.GetTrendsReportAsync(from, to);

        result.Nights.Should().BeEmpty();
        result.Summary.NightCount.Should().Be(0);
        result.Summary.DaysInRange.Should().Be(31); // Jan 1 – Jan 31 inclusive
        result.Summary.CoveragePct.Should().Be(0);

        _glucoseRepo.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetTrendsReportAsync_DeduplicatesSessions_WhenSourceIsNull()
    {
        var night = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        var sessions = new[]
        {
            new SleepSession
            {
                Id           = Guid.NewGuid().ToString(),
                StartTime    = night.AddHours(22),
                EndTime      = night.AddHours(30),
                TotalSleepMs = 400L * 60_000,
                Source       = SleepSource.Garmin,
            },
            new SleepSession
            {
                Id           = Guid.NewGuid().ToString(),
                StartTime    = night.AddHours(22),
                EndTime      = night.AddHours(30),
                TotalSleepMs = 360L * 60_000,
                Source       = SleepSource.Apple,
            },
        };

        _sessionRepo
            .Setup(r => r.GetSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(sessions);

        _glucoseRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, false, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());

        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);

        var result = await _sut.GetTrendsReportAsync(from, to, source: null);

        result.Nights.Should().HaveCount(1);
        result.Summary.DaysInRange.Should().Be(31); // Jan 1 – Jan 31 inclusive
        result.Summary.CoveragePct.Should().BeApproximately(1 * 100.0 / 31, 0.01);
    }

    [Fact]
    public async Task GetTrendsReportAsync_SkipsDedup_WhenSourceIsProvided()
    {
        var night = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        var sessions = new[]
        {
            new SleepSession
            {
                Id           = Guid.NewGuid().ToString(),
                StartTime    = night.AddHours(22),
                EndTime      = night.AddHours(30),
                TotalSleepMs = 400L * 60_000,
                Source       = SleepSource.Oura,
            },
            new SleepSession
            {
                Id           = Guid.NewGuid().ToString(),
                StartTime    = night.AddHours(22),
                EndTime      = night.AddHours(30),
                TotalSleepMs = 360L * 60_000,
                Source       = SleepSource.Oura,
            },
        };

        _sessionRepo
            .Setup(r => r.GetSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(sessions);

        _glucoseRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, false, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());

        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);

        var result = await _sut.GetTrendsReportAsync(from, to, source: SleepSource.Oura);

        result.Nights.Should().HaveCount(2);
    }
}
