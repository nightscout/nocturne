using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Platform;
using Nocturne.API.Services.Sleep;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Services.Sleep;

[Trait("Category", "Unit")]
public class SleepReportServiceTests
{
    private readonly Mock<ISleepSessionRepository> _sessionRepo = new();
    private readonly Mock<ISensorGlucoseRepository> _glucoseRepo = new();
    private readonly Mock<IPatientRecordRepository> _patientRecord = new();
    private readonly Mock<IPatientDeviceRepository> _patientDevices = new();
    private readonly Mock<IAuditContext> _caller = new();
    private readonly NocturneDbContext _db = TestDbContextFactory.CreateInMemoryContext();
    private readonly SleepReportService _sut;

    public SleepReportServiceTests()
    {
        // No patient record by default → reference ranges fall back to adult-female norms.
        _patientRecord
            .Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientRecord?)null);

        _db.TenantId = Guid.NewGuid();

        _patientDevices
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PatientDevice>());
        var demoMode = new Mock<IDemoModeService>();
        demoMode.Setup(d => d.IsEnabled).Returns(false);

        _sut = new SleepReportService(
            _sessionRepo.Object,
            _glucoseRepo.Object,
            new CanonicalGlucoseService(_glucoseRepo.Object, _patientDevices.Object, demoMode.Object),
            _patientRecord.Object,
            new SubjectRepository(_db.AsFactory()),
            _caller.Object,
            TimeProvider.System,
            NullLogger<SleepReportService>.Instance);
    }

    /// <summary>
    /// Two uploaders posting the same two-hour low, one of them <paramref name="offsetSeconds"/>
    /// behind the other, then a return to range.
    /// </summary>
    private void SetupTwoSourceLow(DateTime lowStart, int offsetSeconds)
    {
        var readings = new List<SensorGlucose>();
        for (var i = 0; i < 24; i++)
        {
            var at = lowStart.AddMinutes(i * 5);
            readings.Add(new SensorGlucose { Timestamp = at, Mgdl = 60, DataSource = "xdrip" });
            readings.Add(new SensorGlucose
            {
                Timestamp = at.AddSeconds(offsetSeconds), Mgdl = 60, DataSource = "dexcom-share",
            });
        }
        for (var i = 0; i < 4; i++)
        {
            var at = lowStart.AddMinutes(120 + i * 5);
            readings.Add(new SensorGlucose { Timestamp = at, Mgdl = 100, DataSource = "xdrip" });
            readings.Add(new SensorGlucose
            {
                Timestamp = at.AddSeconds(offsetSeconds), Mgdl = 100, DataSource = "dexcom-share",
            });
        }

        _glucoseRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(readings.OrderBy(r => r.Mills).ToArray());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(30)]
    public async Task GetSingleNightReportAsync_FindsOneLowUploadedByTwoSources(int offsetSeconds)
    {
        var start = new DateTime(2026, 1, 15, 22, 0, 0, DateTimeKind.Utc);
        _sessionRepo
            .Setup(r => r.GetSessionByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SleepSession
            {
                Id = Guid.NewGuid().ToString(),
                StartTime = start,
                EndTime = start.AddHours(8),
                Source = SleepSource.Oura,
            });
        SetupTwoSourceLow(start.AddHours(1), offsetSeconds);

        var result = await _sut.GetSingleNightReportAsync(Guid.NewGuid());

        var hypo = result!.HypoEvents.Should().ContainSingle().Subject;
        hypo.DurationMinutes.Should().Be(120);
        result.OvernightTir!.LowPct.Should().BeApproximately(120.0 / 140 * 100, 0.5);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(30)]
    public async Task GetTrendsReportAsync_CountsOneLowUploadedByTwoSources(int offsetSeconds)
    {
        var start = new DateTime(2026, 1, 15, 22, 0, 0, DateTimeKind.Utc);
        _sessionRepo
            .Setup(r => r.GetSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new SleepSession
                {
                    Id = Guid.NewGuid().ToString(),
                    StartTime = start,
                    EndTime = start.AddHours(8),
                    TotalSleepMs = 400L * 60_000,
                    Source = SleepSource.Oura,
                },
            });
        SetupTwoSourceLow(start.AddHours(1), offsetSeconds);

        var result = await _sut.GetTrendsReportAsync(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc));

        result.Nights.Should().ContainSingle().Which.HypoCount.Should().Be(1);
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
                It.IsAny<int>(), 0, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
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
                It.IsAny<int>(), 0, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());

        await _sut.GetSingleNightReportAsync(Guid.NewGuid());

        _glucoseRepo.Verify(r => r.GetAsync(
            start, end, null, null,
            int.MaxValue, 0, false, null, null,
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
                It.IsAny<int>(), 0, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
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
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
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
                It.IsAny<int>(), 0, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
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
    public async Task GetTrendsReportAsync_ServesThePatientsDays_IncludingTheGapDay()
    {
        var sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        var session = new SleepSession
        {
            Id           = Guid.NewGuid().ToString(),
            StartTime    = new DateTime(2026, 1, 11, 11, 0, 0, DateTimeKind.Utc),
            EndTime      = new DateTime(2026, 1, 11, 19, 0, 0, DateTimeKind.Utc),
            TotalSleepMs = 400L * 60_000,
            Source       = SleepSource.Garmin,
            Timezone     = "Australia/Sydney",
        };

        _sessionRepo
            .Setup(r => r.GetSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([session]);
        _glucoseRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());

        // Sydney is UTC+11 in January, so these bounds are Sydney 10 Jan 00:00 to 12 Jan 23:59:59.999,
        // while their UTC dates are 9 to 12 Jan.
        var from = new DateTime(2026, 1, 9, 13, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2026, 1, 12, 12, 59, 59, 999, DateTimeKind.Utc);

        var result = await _sut.GetTrendsReportAsync(from, to, zone: sydney);

        result.Days.Should().Equal("2026-01-10", "2026-01-11", "2026-01-12");
        result.Nights.Should().ContainSingle().Which.DisplayDate.Should().Be("2026-01-11");
        result.Summary.DaysInRange.Should().Be(3);
        result.Weeks.Sum(w => w.DaysInRange).Should().Be(3);
    }

    [Fact]
    public async Task GetTrendsReportAsync_WithoutAZone_CountsUtcDays()
    {
        _sessionRepo
            .Setup(r => r.GetSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SleepSession>());

        var result = await _sut.GetTrendsReportAsync(
            new DateTime(2026, 1, 9, 13, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 11, 12, 59, 59, 999, DateTimeKind.Utc),
            zone: null);

        result.Days.Should().Equal("2026-01-09", "2026-01-10", "2026-01-11");
    }

    [Fact]
    public async Task GetTrendsReportAsync_ServesTheDaysWithNoSessions()
    {
        _sessionRepo
            .Setup(r => r.GetSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SleepSession>());

        var result = await _sut.GetTrendsReportAsync(
            new DateTime(2026, 1, 9, 13, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 11, 12, 59, 59, 999, DateTimeKind.Utc),
            zone: TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney"));

        result.Days.Should().Equal("2026-01-10", "2026-01-11");
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
                It.IsAny<int>(), 0, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());

        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);

        var result = await _sut.GetTrendsReportAsync(from, to, source: SleepSource.Oura);

        result.Nights.Should().HaveCount(2);
    }

    // ── Weekly buckets follow the caller's regional first day ──────────────

    private void SetupSaturdayAndSundayNights()
    {
        var saturday = new DateTime(2026, 1, 17, 0, 0, 0, DateTimeKind.Utc);
        var sessions = new[] { saturday, saturday.AddDays(1) }
            .Select(day => new SleepSession
            {
                Id           = Guid.NewGuid().ToString(),
                StartTime    = day.AddHours(22),
                EndTime      = day.AddHours(30),
                TotalSleepMs = 400L * 60_000,
                Source       = SleepSource.Oura,
            })
            .ToArray();
        _sessionRepo
            .Setup(r => r.GetSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(sessions);
        _glucoseRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());
    }

    private void CallerIs(string? preferences, string? language = null)
    {
        var id = Guid.CreateVersion7();
        _db.Subjects.Add(new SubjectEntity
        {
            Id = id, Name = "Caller", Preferences = preferences, PreferredLanguage = language,
        });
        _db.SaveChanges();
        _caller.Setup(c => c.SubjectId).Returns(id);
    }

    private async Task<List<(DateTime WeekStart, int NightCount)>> WeeksOfSaturdayAndSundayNights()
    {
        SetupSaturdayAndSundayNights();
        var report = await _sut.GetTrendsReportAsync(
            new DateTime(2026, 1, 11, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 24, 0, 0, 0, DateTimeKind.Utc));
        return report.Weeks.Where(w => w.NightCount > 0).Select(w => (w.WeekStart, w.NightCount)).ToList();
    }

    [Theory]
    [InlineData("""{ "regionFormat": "en-US" }""", null)]
    [InlineData("""{ "regionFormat": "" }""", "en")]
    [InlineData(null, "ja")]
    [InlineData(null, null)]
    [InlineData("""{ "regionFormat": "" }""", null)]
    public async Task GetTrendsReportAsync_StartsWeeksOnSunday_ForASundayRegion(string? preferences, string? language)
    {
        CallerIs(preferences, language);

        var weeks = await WeeksOfSaturdayAndSundayNights();

        weeks.Should().Equal((new DateTime(2026, 1, 11), 1), (new DateTime(2026, 1, 18), 1));
    }

    [Theory]
    [InlineData("""{ "regionFormat": "en-GB" }""", "en")]
    [InlineData("""{ "regionFormat": "de-DE" }""", null)]
    [InlineData(null, "de")]
    public async Task GetTrendsReportAsync_StartsWeeksOnMonday_ForAMondayRegion(string? preferences, string? language)
    {
        CallerIs(preferences, language);

        var weeks = await WeeksOfSaturdayAndSundayNights();

        weeks.Should().Equal((new DateTime(2026, 1, 12), 2));
    }

    [Fact]
    public async Task GetTrendsReportAsync_StartsWeeksOnMonday_ForACallerWithNoPreferences()
    {
        _caller.Setup(c => c.SubjectId).Returns((Guid?)null);

        var weeks = await WeeksOfSaturdayAndSundayNights();

        weeks.Should().Equal((new DateTime(2026, 1, 12), 2));
    }

    [Fact]
    public async Task GetTrendsReportAsync_StartsWeeksOnMonday_ForASubjectWhoChoseAMondayLanguage()
    {
        CallerIs(preferences: null, language: "fr");

        var weeks = await WeeksOfSaturdayAndSundayNights();

        weeks.Should().Equal((new DateTime(2026, 1, 12), 2));
    }
}
