using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Devices;

[Trait("Category", "Unit")]
public class ReservoirEstimationServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IPumpSnapshotRepository> _pumpSnapshots = new();
    private readonly Mock<IBolusRepository> _boluses = new();
    private readonly Mock<ITempBasalRepository> _tempBasals = new();
    private readonly Mock<IBasalSegmentService> _basalSegments = new();
    private readonly CapturingLogger _logger = new();
    private readonly ReservoirEstimationService _sut;

    public ReservoirEstimationServiceTests()
    {
        SetupSnapshots();
        SetupBoluses();
        SetupTempBasals();
        SetupSegments();
        _sut = new ReservoirEstimationService(
            _pumpSnapshots.Object, _boluses.Object, _tempBasals.Object, _basalSegments.Object,
            new FakeTimeProvider(new DateTimeOffset(Now)), _logger);
    }

    [Fact]
    public async Task NoSnapshots_ReturnsNull()
    {
        (await _sut.GetEstimateAsync(null)).Should().BeNull();
    }

    [Fact]
    public async Task FreshExactReading_PassesThroughUnestimated()
    {
        SetupSnapshots(new PumpSnapshot { Reservoir = 42.5, Timestamp = Now.AddMinutes(-3) });

        var estimate = await _sut.GetEstimateAsync(null);

        estimate.Should().Be(new ReservoirEstimate(42.5m, IsLowerBound: false, IsEstimated: false));
    }

    [Fact]
    public async Task OldAnchor_DepletedByBolusesAndTempBasals()
    {
        SetupSnapshots(new PumpSnapshot { Reservoir = 50, Timestamp = Now.AddHours(-10) });
        SetupBoluses(MakeBolus(4), MakeBolus(2));
        // 1 U/hr for 2h = 2U
        SetupTempBasals(MakeTempBasal(rate: 1.0, start: Now.AddHours(-8), end: Now.AddHours(-6)));

        var estimate = await _sut.GetEstimateAsync(null);

        estimate.Should().Be(new ReservoirEstimate(42m, IsLowerBound: false, IsEstimated: true));
    }

    [Fact]
    public async Task CappedReadingsOnly_ReturnsLowerBound()
    {
        SetupSnapshots(new PumpSnapshot { Reservoir = 50, ReservoirDisplay = "50+U", Timestamp = Now.AddMinutes(-5) });

        var estimate = await _sut.GetEstimateAsync(null);

        estimate.Should().Be(new ReservoirEstimate(50m, IsLowerBound: true, IsEstimated: false));
    }

    [Fact]
    public async Task NewerCappedReading_FloorsALowerEstimate()
    {
        SetupSnapshots(
            new PumpSnapshot { Reservoir = 50, ReservoirDisplay = "50+U", Timestamp = Now.AddMinutes(-5) },
            new PumpSnapshot { Reservoir = 60, Timestamp = Now.AddHours(-12) });
        SetupBoluses(MakeBolus(15));

        var estimate = await _sut.GetEstimateAsync(null);

        estimate.Should().Be(new ReservoirEstimate(50m, IsLowerBound: true, IsEstimated: false));
    }

    [Fact]
    public async Task NewerCappedReading_BelowEstimate_KeepsEstimate()
    {
        SetupSnapshots(
            new PumpSnapshot { Reservoir = 50, ReservoirDisplay = "50+U", Timestamp = Now.AddMinutes(-5) },
            new PumpSnapshot { Reservoir = 80, Timestamp = Now.AddHours(-12) });
        SetupBoluses(MakeBolus(10));

        var estimate = await _sut.GetEstimateAsync(null);

        estimate.Should().Be(new ReservoirEstimate(70m, IsLowerBound: false, IsEstimated: true));
    }

    [Fact]
    public async Task NoTempBasalsOrAlgorithmBoluses_FallsBackToScheduledBasal()
    {
        SetupSnapshots(new PumpSnapshot { Reservoir = 100, Timestamp = Now.AddHours(-10) });
        SetupBoluses(MakeBolus(2));
        // 1.0 U/hr over 10h = 10U scheduled
        SetupSegments(new BasalSegment(Mills(Now.AddHours(-10)), Mills(Now), 1.0, 1.0, "Default"));

        var estimate = await _sut.GetEstimateAsync(null);

        estimate.Should().Be(new ReservoirEstimate(88m, IsLowerBound: false, IsEstimated: true));
    }

    [Fact]
    public async Task TempBasalDeclaredEndInFuture_CountsOnlyDeliveredPortion()
    {
        SetupSnapshots(new PumpSnapshot { Reservoir = 50, Timestamp = Now.AddHours(-2) });
        // 2 U/hr declared until 1h from now; only the elapsed 1h (2U) has been delivered
        SetupTempBasals(MakeTempBasal(rate: 2.0, start: Now.AddHours(-1), end: Now.AddHours(1)));

        var estimate = await _sut.GetEstimateAsync(null);

        estimate.Should().Be(new ReservoirEstimate(48m, IsLowerBound: false, IsEstimated: true));
    }

    [Fact]
    public async Task TempBasalStraddlingWindowStart_CountsInWindowPortion_AndSuppressesScheduledFallback()
    {
        SetupSnapshots(new PumpSnapshot { Reservoir = 50, Timestamp = Now.AddHours(-2) });
        // Started 3h before the anchor; 1 U/hr, ends 1h after the anchor → 1U in-window
        SetupTempBasals(MakeTempBasal(rate: 1.0, start: Now.AddHours(-5), end: Now.AddHours(-1)));
        // Scheduled profile must NOT be added on top: a temp basal was running
        SetupSegments(new BasalSegment(Mills(Now.AddHours(-2)), Mills(Now), 5.0, 5.0, "Default"));

        var estimate = await _sut.GetEstimateAsync(null);

        estimate.Should().Be(new ReservoirEstimate(49m, IsLowerBound: false, IsEstimated: true));
    }

    [Fact]
    public async Task DeliveryExceedingAnchor_ClampsToZero()
    {
        SetupSnapshots(new PumpSnapshot { Reservoir = 5, Timestamp = Now.AddHours(-10) });
        SetupBoluses(MakeBolus(8));

        var estimate = await _sut.GetEstimateAsync(null);

        estimate.Should().Be(new ReservoirEstimate(0m, IsLowerBound: false, IsEstimated: true));
    }

    /// <summary>Records warning-level messages. Moq cannot proxy
    /// <c>ILogger&lt;ReservoirEstimationService&gt;</c> because the generic argument is internal.</summary>
    private sealed class CapturingLogger : ILogger<ReservoirEstimationService>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    [Fact]
    public async Task SnapshotFetchAtScanLimit_LogsTruncationWarning()
    {
        // 200 snapshots (the scan limit) — the fetch was likely truncated.
        var snapshots = Enumerable.Range(0, 200)
            .Select(i => new PumpSnapshot { Reservoir = i == 0 ? 42.0 : null, Timestamp = Now.AddMinutes(-i) })
            .ToArray();
        SetupSnapshots(snapshots);

        await _sut.GetEstimateAsync(null);

        _logger.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task BolusFetchAtRecordLimit_LogsTruncationWarning()
    {
        SetupSnapshots(new PumpSnapshot { Reservoir = 50, Timestamp = Now.AddHours(-10) });
        SetupBoluses(Enumerable.Range(0, 5000).Select(_ => MakeBolus(0.001)).ToArray());

        await _sut.GetEstimateAsync(null);

        _logger.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task TempBasalFetchAtRecordLimit_LogsTruncationWarning()
    {
        SetupSnapshots(new PumpSnapshot { Reservoir = 50, Timestamp = Now.AddHours(-10) });
        SetupTempBasals(Enumerable.Range(0, 5000)
            .Select(i => MakeTempBasal(rate: 0.1, start: Now.AddHours(-9).AddSeconds(i), end: Now.AddHours(-9).AddSeconds(i + 1)))
            .ToArray());

        await _sut.GetEstimateAsync(null);

        _logger.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task FetchesBelowLimits_LogNoWarning()
    {
        SetupSnapshots(new PumpSnapshot { Reservoir = 50, Timestamp = Now.AddHours(-10) });
        SetupBoluses(MakeBolus(4));
        SetupTempBasals(MakeTempBasal(rate: 1.0, start: Now.AddHours(-8), end: Now.AddHours(-6)));

        await _sut.GetEstimateAsync(null);

        _logger.Warnings.Should().BeEmpty();
    }

    private static long Mills(DateTime at) => new DateTimeOffset(at, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static Bolus MakeBolus(double insulin) => new() { Insulin = insulin, Kind = BolusKind.Manual };

    private static TempBasal MakeTempBasal(double rate, DateTime start, DateTime end) => new()
    {
        Rate = rate,
        StartTimestamp = start,
        EndTimestamp = end,
    };

    private void SetupSnapshots(params PumpSnapshot[] snapshots) =>
        _pumpSnapshots
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshots.OrderByDescending(s => s.Timestamp).ToArray());

    private void SetupBoluses(params Bolus[] boluses) =>
        _boluses
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, false, It.IsAny<bool>(), It.IsAny<BolusKind?>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(boluses);

    private void SetupTempBasals(params TempBasal[] tempBasals) =>
        _tempBasals
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tempBasals);

    private void SetupSegments(params BasalSegment[] segments) =>
        _basalSegments
            .Setup(s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(segments.ToAsyncEnumerable());
}
