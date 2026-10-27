using Moq;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Clinical.Tests.Treatments;

/// <summary>
/// Tests for the V4-native <see cref="IobCalculator"/> operating on <see cref="Bolus"/>
/// and <see cref="TempBasal"/> records, on the iob.js curve for a record without its own peak and
/// the oref0 exponential curve for one with it.
/// </summary>
public class IobCalculatorTests
{
    private readonly IobCalculator _calculator;
    private readonly Mock<IApsSnapshotRepository> _apsSnapshotRepo;
    private readonly Mock<IPumpSnapshotRepository> _pumpSnapshotRepo;
    private readonly Mock<IBasalSegmentService> _basalSegments;
    private readonly Mock<IStateSpanRepository> _stateSpans = MockStateSpans.Empty();

    private const double DefaultDIA = 3.0;
    private const double DefaultSensitivity = 95.0;
    private const double DefaultBasalRate = 1.0;

    public IobCalculatorTests()
    {
        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings
            .Setup(t => t.GetDIAAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultDIA);

        var sensitivityResolver = new Mock<ISensitivityResolver>();
        sensitivityResolver
            .Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultSensitivity);

        _basalSegments = MockBasalSegments.Flat(DefaultBasalRate);

        _apsSnapshotRepo = new Mock<IApsSnapshotRepository>();
        _pumpSnapshotRepo = new Mock<IPumpSnapshotRepository>();

        // Default: repos return empty results
        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());
        _pumpSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<PumpSnapshot>());

        _calculator = new IobCalculator(
            therapySettings.Object,
            sensitivityResolver.Object,
            _basalSegments.Object,
            _apsSnapshotRepo.Object,
            _pumpSnapshotRepo.Object,
            _stateSpans.Object
        );
    }

    #region CalcBolus Tests

    [Fact]
    public void CalcBolus_RecentBolus_ShouldReturnNonZeroIob()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bolus = new Bolus
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 30 * 60 * 1000).UtcDateTime,
            Insulin = 2.0,
        };

        var result = _calculator.CalcBolus(bolus, now);

        Assert.True(result.IobContrib > 0, "2U bolus 30 min ago should have non-zero IOB");
        Assert.True(result.IobContrib < 2.0, "IOB should be less than full dose after 30 min");
    }

    [Fact]
    public void CalcBolus_ExpiredBolus_ShouldReturnZeroIob()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bolus = new Bolus
        {
            // 4 hours ago - well beyond DIA=3
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 4 * 60 * 60 * 1000).UtcDateTime,
            Insulin = 2.0,
        };

        var result = _calculator.CalcBolus(bolus, now);

        Assert.Equal(0.0, result.IobContrib);
    }

    [Fact]
    public void CalcBolus_ReadsInsulinContext_OverridesProfileDia()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bolus = new Bolus
        {
            // 4 hours ago - beyond profile DIA=3 but within InsulinContext DIA=6
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 4 * 60 * 60 * 1000).UtcDateTime,
            Insulin = 2.0,
            InsulinContext = new TreatmentInsulinContext
            {
                PatientInsulinId = Guid.NewGuid(),
                InsulinName = "Fiasp",
                Dia = 6.0,
                Peak = 75,
                Curve = "rapid-acting",
                Concentration = 100,
            },
        };

        var result = _calculator.CalcBolus(bolus, now);

        Assert.True(result.IobContrib > 0, "Bolus 4hrs ago with DIA=6 should still be active");
    }

    [Fact]
    public void CalcBolus_ZeroInsulin_ShouldReturnZeroIob()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bolus = new Bolus
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 30 * 60 * 1000).UtcDateTime,
            Insulin = 0.0,
        };

        var result = _calculator.CalcBolus(bolus, now);

        Assert.Equal(0.0, result.IobContrib);
        Assert.Equal(0.0, result.ActivityContrib);
    }

    #endregion

    #region TherapySnapshot overloads (async-free chart hot path)

    // A snapshot whose in-memory lookups return the same values as the mocked async resolvers
    // (DIA 3.0, sensitivity 95.0, basal rate 1.0), so the snapshot overloads must produce
    // numerically identical results to the legacy async-resolver path.
    private static TherapySnapshot MatchingSnapshot() => new(
        dia: DefaultDIA,
        carbsPerHour: 30.0,
        timezone: null,
        ccpPercentage: null,
        ccpTimeshiftMs: 0,
        sensitivityEntries: new[] { new ScheduleEntry { TimeAsSeconds = 0, Value = DefaultSensitivity } },
        carbRatioEntries: null,
        basalEntries: new[] { new ScheduleEntry { TimeAsSeconds = 0, Value = DefaultBasalRate } });

    [Fact]
    public void CalcBolus_SnapshotOverload_MatchesAsyncResolverPath()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bolus = new Bolus
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 40 * 60 * 1000).UtcDateTime,
            Insulin = 2.5,
        };

        var viaAsync = _calculator.CalcBolus(bolus, now);
        var viaSnapshot = _calculator.CalcBolus(bolus, MatchingSnapshot(), now);

        Assert.Equal(viaAsync.IobContrib, viaSnapshot.IobContrib, 9);
        Assert.Equal(viaAsync.ActivityContrib, viaSnapshot.ActivityContrib, 9);

        // And it must not touch the async resolvers.
        _basalSegments.Verify(b => b.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void FromBoluses_SnapshotOverload_MatchesAsyncPath()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var boluses = new List<Bolus>
        {
            new() { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 20 * 60 * 1000).UtcDateTime, Insulin = 1.5 },
            new() { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 90 * 60 * 1000).UtcDateTime, Insulin = 3.0 },
        };

        var viaAsync = _calculator.FromBoluses(boluses, now);
        var viaSnapshot = _calculator.FromBoluses(boluses, MatchingSnapshot(), now);

        Assert.Equal(viaAsync.Iob, viaSnapshot.Iob, 9);
        Assert.Equal(viaAsync.Activity!.Value, viaSnapshot.Activity!.Value, 9);
    }

    #endregion

    #region FromBoluses Tests

    [Fact]
    public void FromBoluses_MultipleBoluses_AggregatesIob()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var boluses = new List<Bolus>
        {
            new()
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 60 * 60 * 1000).UtcDateTime,
                Insulin = 2.0,
            },
            new()
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 30 * 60 * 1000).UtcDateTime,
                Insulin = 1.5,
            },
            new()
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 10 * 60 * 1000).UtcDateTime,
                Insulin = 1.0,
            },
        };

        var result = _calculator.FromBoluses(boluses, now);

        Assert.True(result.Iob > 0, "Aggregated IOB should be positive");
        Assert.True(result.Iob < 4.5, "Aggregated IOB should be less than total insulin");
        Assert.Equal("Care Portal", result.Source);

        // LastBolus should be the most recent (10 min ago)
        Assert.NotNull(result.LastBolus);
        Assert.Equal(1.0, result.LastBolus!.Insulin);
    }

    [Fact]
    public void FromBoluses_SkipsZeroInsulinBoluses()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var boluses = new List<Bolus>
        {
            new()
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 30 * 60 * 1000).UtcDateTime,
                Insulin = 0.0,
            },
            new()
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 10 * 60 * 1000).UtcDateTime,
                Insulin = 1.0,
            },
        };

        var result = _calculator.FromBoluses(boluses, now);

        // LastBolus should be the 1.0U bolus, not the 0.0U one
        Assert.NotNull(result.LastBolus);
        Assert.Equal(1.0, result.LastBolus!.Insulin);
    }

    [Fact]
    public void FromBoluses_EmptyList_ReturnsZero()
    {
        var result = _calculator.FromBoluses(new List<Bolus>(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Equal(0.0, result.Iob);
        Assert.Null(result.LastBolus);
    }

    #endregion

    #region CalculateTotalAsync device-value merge

    [Fact]
    public async Task CalculateTotalAsync_DeviceReportsZeroIob_ZeroIsAccepted()
    {
        // IOB of exactly zero from the device is a real value (e.g. after a long suspend) and
        // must win over a local recomputation that still sees recent boluses decaying.
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ApsSnapshot
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 2 * 60 * 1000).UtcDateTime,
                    Iob = 0.0,
                },
            });

        var boluses = new List<Bolus>
        {
            new()
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 30 * 60 * 1000).UtcDateTime,
                Insulin = 2.0,
            },
        };

        var result = await _calculator.CalculateTotalAsync(boluses, null, now);

        Assert.Equal(0.0, result.Iob);
        Assert.Equal("OpenAPS", result.Source);
        Assert.NotNull(result.TreatmentIob);
    }

    [Fact]
    public async Task CalculateTotalAsync_DeviceReportsBasalIob_LocalBasalIobNotAdded()
    {
        // The device's basal IOB already covers its own temp basals; the locally-computed value
        // must not be added on top of it.
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ApsSnapshot
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 2 * 60 * 1000).UtcDateTime,
                    Iob = 2.0,
                    BasalIob = 0.3,
                },
            });

        // An active high temp basal that yields a non-zero local basal IOB.
        var tempBasals = new List<TempBasal>
        {
            new()
            {
                StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 60 * 60 * 1000).UtcDateTime,
                EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(now).UtcDateTime,
                Rate = 3.0,
                ScheduledRate = DefaultBasalRate,
                Origin = TempBasalOrigin.Algorithm,
            },
        };

        var result = await _calculator.CalculateTotalAsync(new List<Bolus>(), tempBasals, now);

        Assert.Equal(2.0, result.Iob);
        Assert.Equal(0.3, result.BasalIob);
    }

    [Fact]
    public async Task CalculateTotalAsync_DeviceReportsNoBasalIob_BasalIobIsNull()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ApsSnapshot
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 2 * 60 * 1000).UtcDateTime,
                    Iob = 2.0,
                    BasalIob = null,
                },
            });

        var tempBasals = new List<TempBasal>
        {
            new()
            {
                StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 60 * 60 * 1000).UtcDateTime,
                EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(now).UtcDateTime,
                Rate = 3.0,
                ScheduledRate = DefaultBasalRate,
                Origin = TempBasalOrigin.Algorithm,
            },
        };

        var result = await _calculator.CalculateTotalAsync(new List<Bolus>(), tempBasals, now);

        Assert.Equal(2.0, result.Iob);
        Assert.Null(result.BasalIob);
        Assert.Null(result.BolusIob);
    }

    [Fact]
    public async Task CalculateTotalAsync_DeviceTotal_IsPassedThroughUnchanged()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _pumpSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new PumpSnapshot
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 2 * 60 * 1000).UtcDateTime,
                    Iob = 1.23456,
                    BolusIob = 0.98765,
                },
            });

        // A suspend that would make the local total negative must not touch the device's.
        var result = await _calculator.CalculateTotalAsync(
            [], [Temp(now - 30 * Minute, now, rate: 0, origin: TempBasalOrigin.Suspended)], now);

        Assert.Equal(1.23456, result.Iob);
        Assert.Equal(0.98765, result.BolusIob);
        Assert.Null(result.BasalIob);
        Assert.Equal("Pump", result.Source);
    }

    [Fact]
    public async Task CalculateTotalAsync_ApsSnapshotWithoutATotal_FallsThroughToThePump()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ApsSnapshot
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 2 * 60 * 1000).UtcDateTime,
                    Iob = null,
                    BasalIob = 0.3,
                },
            });
        _pumpSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new PumpSnapshot
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 3 * 60 * 1000).UtcDateTime,
                    Iob = 1.5,
                },
            });

        var result = await _calculator.CalculateTotalAsync([], null, now);

        Assert.Equal(1.5, result.Iob);
        Assert.Equal("Pump", result.Source);
    }

    [Fact]
    public async Task CalculateTotalAsync_TakesASnapshotStampedAfterTheInstant_OnlyWhenLive()
    {
        // The device's clock runs two minutes ahead of the server's.
        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ApsSnapshot { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(Now + 2 * Minute).UtcDateTime, Iob = 2.0 } });

        var live = await _calculator.CalculateTotalAsync([], null, Now, live: true);
        var past = await _calculator.CalculateTotalAsync([], null, Now);

        Assert.Equal((2.0, "OpenAPS"), (live.Iob, live.Source));
        Assert.Equal((0.0, "Care Portal"), (past.Iob, past.Source));
    }

    [Fact]
    public async Task CalculateTotalAsync_ApsSnapshotWithoutIob_GivesWayToThePump_PerComponent()
    {
        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ApsSnapshot { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(Now - 2 * Minute).UtcDateTime, Cob = 15 } });
        _pumpSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PumpSnapshot { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(Now - 3 * Minute).UtcDateTime, BolusIob = 0.6 } });

        var result = await _calculator.CalculateTotalAsync([], null, Now);

        Assert.Equal((0.6, "Pump"), (result.Iob, result.Source));
    }

    [Fact]
    public async Task CalculateFromTreatmentsAsync_IgnoresARecentDeviceSnapshot()
    {
        // A recent device total of 2.0 U, beside 30 minutes suspended on a 1 U/h schedule:
        // -(0.996296 + 0.988888 + 0.977776 + 0.96296 + 0.94444 + 0.922216) / 12 = -0.483 U from the treatments.
        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ApsSnapshot { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(Now - 60 * 1000).UtcDateTime, Iob = 2.0 } });

        var result = await _calculator.CalculateFromTreatmentsAsync(
            [], [Temp(Now - 30 * Minute, Now, rate: 0, origin: TempBasalOrigin.Suspended)], Now);

        Assert.Equal(-0.483, result.Iob);
        Assert.Equal(-0.483, result.BasalIob);
        Assert.Equal("Care Portal", result.Source);
    }

    [Fact]
    public async Task CalculateTotalAsync_NegativeTotal_IsDisplayedLikeAPositiveOne()
    {
        // 30 minutes suspended on a 1 U/h schedule: -0.483 U, as above.
        var result = await _calculator.CalculateTotalAsync(
            [], [Temp(Now - 30 * Minute, Now, rate: 0, origin: TempBasalOrigin.Suspended)], Now);

        Assert.Equal("-0.48", result.Display);
        Assert.Equal("IOB: -0.48U", result.DisplayLine);
    }

    #endregion

    #region Delivered temp basals

    // Six loop temps 5 min apart, each declaring 30 min at 2 U/h over a 1 U/h schedule. Each cancels
    // the one before, so 1 U/h extra ran for 30 min: six 1/12 U chunks, 5 to 30 min old, on the DIA 3
    // curve, where f(m) = 1 - 0.001852 (m/5)(m/5 + 1) before the 75 min peak:
    // f(5..30) = 0.996296 + 0.988888 + 0.977776 + 0.96296 + 0.94444 + 0.922216 = 5.792576,
    // and 5.792576 / 12 = 0.483.
    private const double LoopStreamBasalIob = 0.483;

    private static List<TempBasal> LoopStream(long now) =>
    [
        .. Enumerable.Range(0, 6).Select(i => new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - (30 - 5 * i) * 60_000L).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - (30 - 5 * i) * 60_000L + 30 * 60_000L).UtcDateTime,
            Rate = 2.0,
            ScheduledRate = DefaultBasalRate,
            Origin = TempBasalOrigin.Algorithm,
            Device = "openaps://Trio",
        }),
    ];

    [Fact]
    public async Task CalculateTotalAsync_LoopTempsCancellingEachOther_CountsOnlyWhatRan()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var result = await _calculator.CalculateTotalAsync([], LoopStream(now), now);

        Assert.NotNull(result.BasalIob);
        Assert.Equal(LoopStreamBasalIob, result.BasalIob!.Value, 0.002);
    }

    [Fact]
    public void IobCobSeries_LoopTempsCancellingEachOther_CountsOnlyWhatRan()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var timeline = new TherapyTimeline([new TherapySegment(now - 60 * 60_000L, now + 1, MatchingSnapshot())]);

        var ticks = IobCobSeries.Build(
            _calculator, Mock.Of<ICobCalculator>(), [], [], LoopStream(now), timeline, new ScheduledBasal(timeline.BasalSegments(), []), null, now, now, now, 5 * 60_000L);

        Assert.Equal(LoopStreamBasalIob, ticks.Single().Iob, 0.002);
    }

    [Fact]
    public async Task CalculateTotalAsync_MirrorOverlappingTheLoop_CountsTheLoopOnly()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var loop = LoopStream(now);
        foreach (var tb in loop)
            tb.OriginEvidence = TempBasalOriginEvidence.Snapshot;
        var mirror = LoopStream(now - 2 * 60_000L);
        foreach (var tb in mirror)
        {
            tb.Device = "tidepool";
            tb.Rate = 3.0;
        }

        var result = await _calculator.CalculateTotalAsync([], [.. loop, .. mirror], now);

        // The mirror's first two minutes at 2 U/h over schedule run before the loop's first temp:
        // 1/15 U, 32 min old, keeps f(32) = 1 - 0.001852 * 6.4 * 7.4 = 0.912289 of itself.
        var mirrorOnly = 0.912289 / 15;
        Assert.NotNull(result.BasalIob);
        Assert.Equal(LoopStreamBasalIob + mirrorOnly, result.BasalIob!.Value, 0.002);
    }

    #endregion

    #region Net basal IOB

    private const long Minute = 60 * 1000;

    // A fixed instant on a five-minute boundary; the schedule and sensitivity are flat.
    private const long Now = 1_750_000_200_000;

    private static TempBasal Temp(long start, long? end, double rate, double? scheduledRate = null,
        TempBasalOrigin origin = TempBasalOrigin.Algorithm) => new()
    {
        StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(start).UtcDateTime,
        EndTimestamp = end is { } e ? DateTimeOffset.FromUnixTimeMilliseconds(e).UtcDateTime : null,
        Rate = rate,
        ScheduledRate = scheduledRate,
        Origin = origin,
    };

    private IobResult NetBasal(List<TempBasal> pieces, double profileRate = DefaultBasalRate) =>
        NetBasal(pieces, [new BasalSegment(Now - 24 * 60 * Minute, Now + 1, profileRate, profileRate, "Default")]);

    private IobResult NetBasal(List<TempBasal> pieces, BasalSegment[] profile) =>
        _calculator.FromTempBasals(pieces, MatchingSnapshot(), new ScheduledBasal(profile, []).Under, Now, Now);

    // Every expectation is on the DIA 3 h curve, where minutes are unscaled. Before the 75 min peak a
    // dose m minutes old keeps f(m) = 1 - 0.001852 (m/5)(m/5 + 1) of itself:
    // f(5) = 0.996296, f(10) = 0.988888, f(12) = 0.98488768, f(15) = 0.977776, f(17) = 0.97229408,
    // f(20) = 0.96296, f(25) = 0.94444, f(30) = 0.922216, f(35) = 0.896288, f(40) = 0.866656,
    // f(45) = 0.83332, f(50) = 0.79628, f(55) = 0.755536, f(60) = 0.711088.
    // Its activity is 95 * units * (2 / 3 / 60 / 75) * m = 95 * units * m / 6750.

    [Fact]
    public async Task RunningTemp_DeliversUpToTheTick()
    {
        // Running 10 minutes at 2 U/h on a 1 U/h schedule: two chunks of 1/12 U, 10 and 5 minutes old.
        // (0.988888 + 0.996296) / 12 = 0.165432.
        var result = await _calculator.CalculateTotalAsync([], [Temp(Now - 10 * Minute, end: null, rate: 2.0)], Now);

        Assert.Equal(0.165, result.BasalIob);
        Assert.Equal(0.165, result.Iob);
    }

    [Fact]
    public void Suspend_GivesNegativeBasalIob()
    {
        // 30 minutes suspended on a 1 U/h schedule withholds six chunks of 1/12 U, 5 to 30 minutes old:
        // -(0.996296 + 0.988888 + 0.977776 + 0.96296 + 0.94444 + 0.922216) / 12 = -5.792576 / 12 = -0.482715.
        // Activity: -95 / 12 * (5 + 10 + 15 + 20 + 25 + 30) / 6750 = -0.123148.
        var result = NetBasal([Temp(Now - 30 * Minute, Now, rate: 1.5, origin: TempBasalOrigin.Suspended)]);

        Assert.Equal(-0.483, result.BasalIob);
        Assert.Equal(-0.123148, result.Activity!.Value, 6);
        Assert.True(result.Activity < 0);
    }

    [Fact]
    public void LowTemp_GivesNegativeBasalIob()
    {
        // 0.4 U/h on a 1 U/h schedule withholds 0.6 / 12 = 0.05 U a chunk, 10 and 5 minutes old:
        // -0.05 * (0.988888 + 0.996296) = -0.099259.
        var result = NetBasal([Temp(Now - 10 * Minute, Now, rate: 0.4)]);

        Assert.Equal(-0.099, result.BasalIob);
    }

    [Fact]
    public void OneChunkTemp_EqualsABolusOfItsNetUnits()
    {
        // Five minutes at 3 U/h on a 1 U/h schedule is 2 / 12 = 1/6 U, delivered 30 minutes ago:
        // 0.922216 / 6 = 0.153703, and activity 95 / 6 * 30 / 6750 = 0.070370.
        var start = Now - 30 * Minute;

        var temp = NetBasal([Temp(start, start + 5 * Minute, rate: 3.0)]);
        var bolus = _calculator.CalcBolus(
            new Bolus { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(start).UtcDateTime, Insulin = 1.0 / 6 },
            MatchingSnapshot(),
            Now);

        Assert.Equal(0.154, temp.BasalIob);
        Assert.Equal(0.070370, temp.Activity!.Value, 6);
        Assert.Equal(Math.Round(bolus.IobContrib, 3), temp.BasalIob);
        Assert.Equal(bolus.ActivityContrib, temp.Activity!.Value, 12);
    }

    [Fact]
    public void EndedTemp_DecaysOnTheInsulinCurve()
    {
        // 1/6 U delivered an hour ago keeps 0.711088 / 6 = 0.118515 on the curve. A linear decay over
        // the 3 h DIA would keep 1/6 * (1 - 60 / 180) = 0.111.
        var result = NetBasal([Temp(Now - 60 * Minute, Now - 55 * Minute, rate: 3.0)]);

        Assert.Equal(0.119, result.BasalIob);
    }

    [Fact]
    public void TempEndingAfterTheTick_StopsDeliveringAtTheTick()
    {
        // Only the two chunks before the tick count, as for a temp running 10 minutes: 0.165432.
        var result = NetBasal([Temp(Now - 10 * Minute, Now + 20 * Minute, rate: 2.0)]);

        Assert.Equal(0.165, result.BasalIob);
    }

    [Fact]
    public void PartialLastChunk_CountsOnlyItsMinutes()
    {
        // Seven minutes at 1 U/h over the schedule: 5/60 U delivered 17 minutes ago, then 2/60 U 12
        // minutes ago. 0.97229408 / 12 + 0.98488768 / 30 = 0.081025 + 0.032830 = 0.113854. A full last
        // chunk would add 0.98488768 / 12 instead, 0.163.
        var result = NetBasal([Temp(Now - 17 * Minute, Now - 10 * Minute, rate: 2.0)]);

        Assert.Equal(0.114, result.BasalIob);
    }

    [Fact]
    public async Task EvaluatedAfterTheCutoff_CountsDeliveryOnlyUpToIt()
    {
        // A temp running 10 minutes at 1 U/h over the schedule, delivered up to Now and read 30 minutes
        // later: two chunks of 1/12 U, 40 and 35 minutes old. (0.866656 + 0.896288) / 12 = 0.146912, and
        // activity 95 / 12 * (40 + 35) / 6750 = 0.087963. Delivering on to the reading would add six
        // more chunks.
        var later = Now + 30 * Minute;
        var running = Temp(Now - 10 * Minute, end: null, rate: 2.0);

        var viaSnapshot = _calculator.FromTempBasals(
            [Temp(Now - 10 * Minute, later, rate: 2.0)], MatchingSnapshot(), (_, _) => DefaultBasalRate, later, Now);
        var viaAsync = await _calculator.CalculateTotalAsync([], [running], later, deliveredUntil: Now);

        Assert.Equal(0.147, viaSnapshot.BasalIob);
        Assert.Equal(0.087963, viaSnapshot.Activity!.Value, 6);
        Assert.Equal(0.147, viaAsync.BasalIob);
        Assert.Equal(0.087963, viaAsync.Activity!.Value, 6);
    }

    [Fact]
    public void TempAfterTheTick_ContributesNothing()
    {
        var result = NetBasal([Temp(Now + 5 * Minute, Now + 35 * Minute, rate: 3.0)]);

        Assert.Null(result.BasalIob);
        Assert.Equal(0.0, result.Activity);
    }

    [Fact]
    public void ScheduleChange_SetsTheBaselineOfEachChunk()
    {
        // 1 U/h for 30 minutes; the schedule is 2 U/h until 20 minutes ago and 1 U/h after. Only the
        // chunks 30 and 25 minutes old withhold 1/12 U: -(0.922216 + 0.94444) / 12 = -0.155555. Read
        // once at the temp's start, the schedule would make all six withhold it, -0.483.
        var switchAt = Now - 20 * Minute;

        var result = NetBasal(
            [Temp(Now - 30 * Minute, Now, rate: 1.0)],
            [
                new BasalSegment(Now - 24 * 60 * Minute, switchAt, 2.0, 2.0, "Default"),
                new BasalSegment(switchAt, Now + 1, 1.0, 1.0, "Default"),
            ]);

        Assert.Equal(-0.156, result.BasalIob);
    }

    [Fact]
    public void ScheduledRateOnTheTemp_WinsOverTheProfile()
    {
        // 2 U/h against the temp's own 0.5 U/h schedule for five minutes: 1.5 / 12 = 0.125 U, 5 minutes
        // old, 0.125 * 0.996296 = 0.124537. Against the profile's 2 U/h it would be 0.
        var result = NetBasal([Temp(Now - 5 * Minute, Now, rate: 2.0, scheduledRate: 0.5)], profileRate: 2.0);

        Assert.Equal(0.125, result.BasalIob);
    }

    [Fact]
    public async Task CalculateTotalAsync_AnOverrideScalesTheSchedule_UnderATempWithNoRateOfItsOwn()
    {
        // 1 U/h for five minutes against the 1 U/h schedule halved: 0.5 / 12 U, 5 minutes old,
        // 0.5 / 12 * 0.996296 = 0.041512. Against the unscaled schedule it would be 0.
        HalvingOverride();

        var result = await _calculator.CalculateTotalAsync([], [Temp(Now - 5 * Minute, Now, rate: 1.0)], Now);

        Assert.Equal(0.042, result.BasalIob);
    }

    [Fact]
    public async Task CalculateTotalAsync_AtSeveralInstantsOverTheSameTemps_ReadsTheScheduleOnce()
    {
        HalvingOverride();
        List<TempBasal> temps = [Temp(Now - 5 * Minute, Now, rate: 1.0)];

        foreach (var at in new[] { Now, Now + 30 * Minute, Now + 60 * Minute })
            await _calculator.CalculateTotalAsync([], temps, at, deliveredUntil: Now);

        _stateSpans.Verify(
            s => s.GetByCategories(It.IsAny<IEnumerable<StateSpanCategory>>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _basalSegments.Verify(
            s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private void HalvingOverride() =>
        _stateSpans
            .Setup(s => s.GetByCategories(It.IsAny<IEnumerable<StateSpanCategory>>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<StateSpanCategory, List<StateSpan>>
            {
                [StateSpanCategory.Override] =
                [
                    new StateSpan
                    {
                        Category = StateSpanCategory.Override,
                        StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(Now - 60 * Minute).UtcDateTime,
                        Metadata = new Dictionary<string, object> { ["insulinNeedsScaleFactor"] = 0.5 },
                    },
                ],
            });

    [Fact]
    public void InsulinContextDiaWithoutAPeak_OverridesTheProfileOnTheIobJsCurve()
    {
        // 30 minutes at 1 U/h over the schedule, ended 30 minutes ago: six 1/12 U chunks, 60 to 35
        // minutes old. On DIA 5 h minutes scale by 3/5, to 36, 33, 30, 27, 24, 21:
        // f = 0.89065792 + 0.90710368 + 0.922216 + 0.93599488 + 0.94844032 + 0.95955232 = 5.56396512,
        // / 12 = 0.463664. On the profile's 3 h: (0.711088 + 0.755536 + 0.79628 + 0.83332 + 0.866656
        // + 0.896288) / 12 = 4.859168 / 12 = 0.404931.
        var temp = Temp(Now - 60 * Minute, Now - 30 * Minute, rate: 2.0);
        var withoutContext = NetBasal([temp]);

        temp.InsulinContext = new TreatmentInsulinContext { Dia = 5.0, Curve = "rapid-acting", Concentration = 100 };
        var withContext = NetBasal([temp]);

        Assert.Equal(0.405, withoutContext.BasalIob);
        Assert.Equal(0.464, withContext.BasalIob);
    }

    [Fact]
    public void SuspendLongerThanDia_CountsExactlyTheLastDiaOfChunks()
    {
        // Suspended for 5 h on a 1 U/h schedule. Only chunks under 180 minutes old are on the curve,
        // the 35 from 5 to 175 minutes old, each -1/12 U.
        // Before the peak, ages 5k for k = 1..14: sum (1 - 0.001852 k(k+1)) = 14 - 0.001852 * 1120 = 11.92576.
        // After it, ages 75 + 5x for x = 0..20: sum (0.001323 x^2 - 0.054233 x + 0.55556)
        //   = 0.001323 * 2870 - 0.054233 * 210 + 21 * 0.55556 = 4.07484.
        // IOB: -(11.92576 + 4.07484) / 12 = -16.0006 / 12 = -1.333383.
        // Activity before the peak: -95 / 12 * 525 / 6750 = -0.615741. After it, each chunk gives
        // -95 / 12 * (2/180 - 5x * 2/180/105), summing to -95 / 12 * (21 * 2/180 - 1050 * 2/180/105)
        //   = -95 / 12 * 0.122222 = -0.967593. Total -1.583333. The chunk 175 minutes old holds 0.0001
        // of its insulin but -0.004190 of activity, so skipping it moves only the activity.
        var result = NetBasal([Temp(Now - 300 * Minute, Now, rate: 1.0, origin: TempBasalOrigin.Suspended)]);

        Assert.Equal(-1.333, result.BasalIob);
        Assert.Equal(-1.583333, result.Activity!.Value, 6);
    }

    [Fact]
    public async Task CalculateTotalAsync_BasalIob_MatchesTheSnapshotPath()
    {
        // 1.8 U/h for 30 minutes then 0.2 U/h for 20, on a 1 U/h schedule: +0.8 / 12 U a chunk at 50 to
        // 25 minutes old, -0.8 / 12 U at 20 to 5. 0.8 / 12 * ((0.79628 + 0.83332 + 0.866656 + 0.896288
        // + 0.922216 + 0.94444) - (0.96296 + 0.977776 + 0.988888 + 0.996296)) = 0.8 / 12 * (5.2592
        // - 3.92592) = 0.088885. With no bolus on board the total is that net basal IOB.
        var viaAsync = await _calculator.CalculateTotalAsync(
            [], [Temp(Now - 50 * Minute, Now - 20 * Minute, rate: 1.8), Temp(Now - 20 * Minute, end: null, rate: 0.2)], Now);
        var viaSnapshot = NetBasal(
            [Temp(Now - 50 * Minute, Now - 20 * Minute, rate: 1.8), Temp(Now - 20 * Minute, Now, rate: 0.2)]);

        Assert.Equal(0.089, viaSnapshot.BasalIob);
        Assert.Equal(0.089, viaAsync.BasalIob);
        Assert.Equal(0.089, viaAsync.Iob);
        Assert.Equal(0.0, viaAsync.BolusIob);
        Assert.Equal(viaSnapshot.Activity!.Value, viaAsync.Activity!.Value, 12);
    }

    #endregion

    #region Insulin curves

    private static Bolus BolusAt(long mills, double units, TreatmentInsulinContext? context = null) => new()
    {
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime,
        Insulin = units,
        InsulinContext = context,
    };

    private static Bolus BolusWithPeak(int peak, double dia) =>
        BolusAt(Now, 1.0, new TreatmentInsulinContext { Dia = dia, Peak = peak });

    private IobContribution MinutesAfter(Bolus bolus, double minutes) =>
        _calculator.CalcBolus(bolus, MatchingSnapshot(), bolus.Mills + (long)Math.Round(minutes * Minute));

    public static TheoryData<int, double> RecordPeaksAndDias()
    {
        var data = new TheoryData<int, double>();
        for (var peak = 35; peak <= 120; peak += 5)
        {
            for (var dia = 3; dia <= 10; dia++)
                data.Add(peak, dia);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(RecordPeaksAndDias))]
    public void ARecordPeak_GivesACurveWithNoStepThatEndsAtItsDia(int peak, double dia)
    {
        var bolus = BolusWithPeak(peak, dia);
        var end = Math.Max(dia, 5) * 60;
        var steepest = Enumerable.Range(0, (int)end * 10)
            .Max(tenths => MinutesAfter(bolus, tenths / 10.0).ActivityContrib) / DefaultSensitivity;

        Assert.Equal(1.0, MinutesAfter(bolus, 0).IobContrib, 9);
        for (var minute = 0; minute < end + 30; minute++)
        {
            var step = MinutesAfter(bolus, minute).IobContrib - MinutesAfter(bolus, minute + 1).IobContrib;
            Assert.InRange(step, 0, steepest * 1.001);
        }
        Assert.Equal(0.0, MinutesAfter(bolus, end).IobContrib);
    }

    [Theory]
    [InlineData(55, 5.0)]
    [InlineData(75, 3.0)]
    [InlineData(120, 8.0)]
    public void ARecordPeak_ActivityIsMinusTheSlopeOfIobTimesSensitivity(int peak, double dia)
    {
        var bolus = BolusWithPeak(peak, dia);

        foreach (var minute in new[] { 10.0, 45, 90, 180, 280 })
        {
            var slope = MinutesAfter(bolus, minute - 0.5).IobContrib - MinutesAfter(bolus, minute + 0.5).IobContrib;
            var activity = MinutesAfter(bolus, minute).ActivityContrib;

            Assert.Equal(slope * DefaultSensitivity, activity, activity * 1e-3);
        }
    }

    [Theory]
    [InlineData(55, 5.0)]
    [InlineData(55, 9.0)]
    [InlineData(90, 6.0)]
    public void ARecordPeak_IsInRealMinutes(int peak, double dia)
    {
        var bolus = BolusWithPeak(peak, dia);

        var busiest = Enumerable.Range(0, (int)(dia * 60)).MaxBy(minute => MinutesAfter(bolus, minute).ActivityContrib);

        Assert.Equal(peak, busiest);
    }

    [Theory]
    [InlineData(10, 35)]
    [InlineData(200, 120)]
    public void ARecordPeak_IsHeldToOref0sLimits(int peak, int held)
    {
        foreach (var minute in new[] { 20.0, 60, 150 })
        {
            var expected = MinutesAfter(BolusWithPeak(held, 5.0), minute);
            var actual = MinutesAfter(BolusWithPeak(peak, 5.0), minute);

            Assert.Equal((expected.IobContrib, expected.ActivityContrib), (actual.IobContrib, actual.ActivityContrib));
        }
    }

    [Fact]
    public void ARecordPeak_RunsTheCurveForAtLeastFiveHours()
    {
        var bolus = BolusWithPeak(55, 3.0);

        Assert.True(MinutesAfter(bolus, 240).IobContrib > 0);
        Assert.Equal(0.0, MinutesAfter(bolus, 300).IobContrib);
    }

    [Fact]
    public void ATempWithItsOwnPeak_IsOnBoardAsLongAsABolusOfItsChunks()
    {
        // 2 U/h on a 1 U/h schedule for 10 minutes, ended 4 h ago: two 1/12 U chunks, past the record's
        // 3 h DIA but inside the curve's 5 h.
        var context = new TreatmentInsulinContext { Dia = 3.0, Peak = 55 };
        var temp = Temp(Now - 250 * Minute, Now - 240 * Minute, rate: 2.0);
        temp.InsulinContext = context;
        var chunks = new List<Bolus>
        {
            BolusAt(Now - 250 * Minute, 1.0 / 12, context),
            BolusAt(Now - 245 * Minute, 1.0 / 12, context),
        };

        var basal = NetBasal([temp]);
        var asBoluses = _calculator.FromBoluses(chunks, MatchingSnapshot(), Now);

        Assert.True(basal.BasalIob > 0);
        Assert.Equal(asBoluses.Iob, basal.BasalIob!.Value);
        Assert.Equal(asBoluses.Activity!.Value, basal.Activity!.Value, 12);
    }

    [Fact]
    public void ARecordWithoutAPeak_KeepsTheIobJsCurve_OnEveryPath()
    {
        // 30 minutes on DIA 3 h: f(30) = 0.922216, activity 95 * 30 / 6750.
        var bolus = BolusAt(Now - 30 * Minute, 1.0);
        var withoutAPeak = BolusAt(Now - 30 * Minute, 1.0, new TreatmentInsulinContext { Dia = DefaultDIA });

        foreach (var result in new[]
                 {
                     _calculator.CalcBolus(bolus, Now),
                     _calculator.CalcBolus(withoutAPeak, Now),
                     _calculator.CalcBolus(bolus, MatchingSnapshot(), Now),
                 })
        {
            Assert.Equal(0.922216, result.IobContrib, 12);
            Assert.Equal(95.0 * 30 / 6750, result.ActivityContrib, 12);
        }
    }

    #endregion
}
