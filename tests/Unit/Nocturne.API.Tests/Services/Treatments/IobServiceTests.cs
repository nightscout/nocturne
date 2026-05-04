using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// Complete IOB calculation tests with 1:1 legacy JavaScript compatibility.
/// Tests exact algorithms from <c>ClientApp/mocha-tests/iob.test.js</c> against the
/// snapshot-based <see cref="IobService"/> overloads.
/// </summary>
[Parity("iob.test.js")]
public class IobServiceTests
{
    private readonly IobService _iobService;
    private readonly Mock<IApsSnapshotRepository> _apsSnapshotRepo;
    private readonly Mock<IPumpSnapshotRepository> _pumpSnapshotRepo;
    private readonly TherapySnapshot _defaultSnapshot;

    private const double DefaultDIA = 3.0;
    private const double DefaultSensitivity = 95.0;
    private const double DefaultBasalRate = 1.0;

    public IobServiceTests()
    {
        _defaultSnapshot = BuildSnapshot(DefaultDIA, DefaultSensitivity, DefaultBasalRate);

        _apsSnapshotRepo = new Mock<IApsSnapshotRepository>();
        _pumpSnapshotRepo = new Mock<IPumpSnapshotRepository>();

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());
        _pumpSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<PumpSnapshot>());

        _iobService = new IobService(
            _apsSnapshotRepo.Object,
            _pumpSnapshotRepo.Object,
            BuildTimelineResolver(_defaultSnapshot).Object
        );
    }

    private static TherapySnapshot BuildSnapshot(double dia, double sens, double basal)
    {
        return new TherapySnapshot(
            dia: dia,
            peakMinutes: TherapySnapshot.DefaultPeakMinutes,
            carbsPerHour: TherapySnapshot.DefaultCarbsPerHour,
            timezone: null,
            ccpPercentage: null,
            ccpTimeshiftMs: 0,
            sensitivityEntries: new[] { new ScheduleEntry { Value = sens, TimeAsSeconds = 0 } },
            carbRatioEntries: null,
            basalEntries: new[] { new ScheduleEntry { Value = basal, TimeAsSeconds = 0 } }
        );
    }

    private static Mock<ITherapyTimelineResolver> BuildTimelineResolver(TherapySnapshot snapshot)
    {
        var resolver = new Mock<ITherapyTimelineResolver>();
        resolver
            .Setup(r => r.GetSnapshotAtAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        resolver
            .Setup(r => r.BuildAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long from, long to, string? _, CancellationToken _) =>
                new TherapyTimeline(new[] { new TherapySegment(from, to, snapshot) })
            );
        return resolver;
    }

    private static IobService BuildService(TherapySnapshot snapshot)
    {
        var apsRepo = new Mock<IApsSnapshotRepository>();
        apsRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());
        var pumpRepo = new Mock<IPumpSnapshotRepository>();
        pumpRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<PumpSnapshot>());

        return new IobService(apsRepo.Object, pumpRepo.Object, BuildTimelineResolver(snapshot).Object);
    }

    [Fact]
    public void CalcTreatment_SingleBolusRightAfter_ShouldReturn1Point00IOB()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment { Mills = time - 1, Insulin = 1.0 };

        var result = _iobService.CalcTreatment(treatment, time, _defaultSnapshot);

        Assert.Equal(1.0, result.IobContrib, 2);
    }

    [Fact]
    public void CalcTreatment_After1Hour_ShouldHaveLessIOBThan1()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment { Mills = time - 1, Insulin = 1.0 };

        var result = _iobService.CalcTreatment(treatment, time + 60 * 60 * 1000, _defaultSnapshot);

        Assert.True(result.IobContrib < 1.0);
        Assert.True(result.IobContrib > 0.0);
    }

    [Fact]
    public void CalcTreatment_After3Hours_ShouldHaveZeroIOB()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment { Mills = time - 1, Insulin = 1.0 };

        var result = _iobService.CalcTreatment(treatment, time + 3 * 60 * 60 * 1000, _defaultSnapshot);

        Assert.Equal(0.0, result.IobContrib, 3);
    }

    [Fact]
    public void CalcTreatment_NoNegativeIOB_WhenApproachingZero()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment { Mills = time, Insulin = 5.0 };

        var result = _iobService.CalcTreatment(treatment, time + 3 * 60 * 60 * 1000 - 90 * 1000, _defaultSnapshot);

        Assert.True(result.IobContrib >= 0.0);
    }

    [Fact]
    public void CalcTreatment_4HourDIA_ShouldUseCorrectDuration()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshot = BuildSnapshot(dia: 4.0, sens: DefaultSensitivity, basal: DefaultBasalRate);
        var service = BuildService(snapshot);
        var treatment = new Treatment { Mills = time - 1, Insulin = 1.0 };

        var rightAfter = service.CalcTreatment(treatment, time, snapshot);
        var afterHour = service.CalcTreatment(treatment, time + 60 * 60 * 1000, snapshot);

        Assert.Equal(1.0, rightAfter.IobContrib, 2);
        Assert.True(afterHour.IobContrib > 0.5);
    }

    [Fact]
    public void FromTreatments_MultipleTreatments_ShouldAggregateCorrectly()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Mills = time - 60 * 60 * 1000, Insulin = 2.0 },
            new() { Mills = time - 30 * 60 * 1000, Insulin = 1.5 },
            new() { Mills = time - 10 * 60 * 1000, Insulin = 1.0 },
        };

        var result = _iobService.FromTreatments(treatments, time, _defaultSnapshot);

        Assert.True(result.Iob > 0);
        Assert.True(result.Iob < 4.5);
        Assert.Equal("Care Portal", result.Source);
    }

    [Fact]
    public void FromTreatments_WithBasalTreatments_ShouldCalculateBasalIOB()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new()
            {
                Mills = time - 60 * 60 * 1000,
                EventType = "Temp Basal",
                Absolute = 1.5,
                Duration = 120,
            },
        };

        var result = _iobService.FromTreatments(treatments, time, _defaultSnapshot);

        Assert.True(result.BasalIob.HasValue);
        Assert.True(result.BasalIob.Value > 0);
    }

    #region ApsSnapshot IOB Priority Tests

    [Fact]
    public async Task CalculateTotal_UsesApsSnapshotIob_WhenAvailable()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Mills = time - 60 * 60 * 1000, Insulin = 1.0 },
        };

        var apsSnapshot = new ApsSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time - 5 * 60 * 1000).UtcDateTime,
            Iob = 1.5,
            BasalIob = -0.3,
            AidAlgorithm = AidAlgorithm.Loop,
            Device = "loop://test",
        };

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { apsSnapshot });

        var result = await _iobService.CalculateTotalAsync(treatments, time);

        Assert.Equal(1.5, result.Iob);
        Assert.Equal("Loop", result.Source);
        Assert.True(result.TreatmentIob.HasValue);
    }

    [Fact]
    public async Task CalculateTotal_UsesOpenApsSnapshot_WhenAvailable()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Mills = time - 60 * 60 * 1000, Insulin = 1.0 },
        };

        var apsSnapshot = new ApsSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time - 5 * 60 * 1000).UtcDateTime,
            Iob = 0.5,
            BasalIob = -0.298,
            AidAlgorithm = AidAlgorithm.OpenAps,
            Device = "openaps://test",
        };

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { apsSnapshot });

        var result = await _iobService.CalculateTotalAsync(treatments, time);

        Assert.Equal(0.5, result.Iob);
        Assert.Equal("OpenAPS", result.Source);
    }

    [Fact]
    public async Task CalculateTotal_UsesAapsSnapshot_WhenAvailable()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Mills = time - 60 * 60 * 1000, Insulin = 1.0 },
        };

        var apsSnapshot = new ApsSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time - 5 * 60 * 1000).UtcDateTime,
            Iob = 0.8,
            BasalIob = -0.1,
            AidAlgorithm = AidAlgorithm.AndroidAps,
            Device = "aaps://test",
        };

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { apsSnapshot });

        var result = await _iobService.CalculateTotalAsync(treatments, time);

        Assert.Equal(0.8, result.Iob);
        Assert.Equal("OpenAPS", result.Source);
    }

    [Fact]
    public async Task CalculateTotal_StaleApsSnapshot_FallsThroughToTreatments()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Mills = time - 30 * 60 * 1000, Insulin = 2.0 },
        };

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());

        var result = await _iobService.CalculateTotalAsync(treatments, time);

        Assert.True(result.Iob > 0);
        Assert.Equal("Care Portal", result.Source);
    }

    #endregion

    #region PumpSnapshot IOB Tests

    [Fact]
    public async Task CalculateTotal_UsesPumpSnapshotIob_WhenNoApsSnapshot()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Mills = time - 60 * 60 * 1000, Insulin = 1.0 },
        };

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());

        var pumpSnapshot = new PumpSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time - 5 * 60 * 1000).UtcDateTime,
            Iob = 0.87,
            BolusIob = 0.87,
            Device = "pump://test",
        };

        _pumpSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { pumpSnapshot });

        var result = await _iobService.CalculateTotalAsync(treatments, time);

        Assert.Equal(0.87, result.Iob);
        Assert.Equal("Pump", result.Source);
    }

    [Fact]
    public async Task CalculateTotal_PumpSnapshotUsesBolusIob_WhenIobIsNull()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>();

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());

        var pumpSnapshot = new PumpSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time - 5 * 60 * 1000).UtcDateTime,
            Iob = null,
            BolusIob = 1.23,
            Device = "pump://test",
        };

        _pumpSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { pumpSnapshot });

        var result = await _iobService.CalculateTotalAsync(treatments, time);

        Assert.Equal(1.23, result.Iob);
        Assert.Equal("Pump", result.Source);
    }

    [Fact]
    public async Task CalculateTotal_ApsSnapshotTakesPriority_OverPumpSnapshot()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>();

        var apsSnapshot = new ApsSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time - 5 * 60 * 1000).UtcDateTime,
            Iob = 1.5,
            AidAlgorithm = AidAlgorithm.Loop,
            Device = "loop://test",
        };

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { apsSnapshot });

        var pumpSnapshot = new PumpSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time - 5 * 60 * 1000).UtcDateTime,
            Iob = 0.5,
            Device = "pump://test",
        };

        _pumpSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { pumpSnapshot });

        var result = await _iobService.CalculateTotalAsync(treatments, time);

        Assert.Equal(1.5, result.Iob);
        Assert.Equal("Loop", result.Source);
    }

    #endregion

    #region CalculateTotal Fallback Tests

    [Fact]
    public async Task CalculateTotal_FallsBackToTreatments_WhenNoSnapshots()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Mills = time - 30 * 60 * 1000, Insulin = 2.0 },
        };

        var result = await _iobService.CalculateTotalAsync(treatments, time);

        Assert.True(result.Iob > 0);
        Assert.Equal("Care Portal", result.Source);
    }

    [Fact]
    public async Task CalculateTotal_CombinesDeviceIobAndTreatmentIob()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Mills = time - 60 * 60 * 1000, Insulin = 1.0 },
        };

        var apsSnapshot = new ApsSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time - 5 * 60 * 1000).UtcDateTime,
            Iob = 1.5,
            AidAlgorithm = AidAlgorithm.Loop,
            Device = "loop://test",
        };

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { apsSnapshot });

        var result = await _iobService.CalculateTotalAsync(treatments, time);

        Assert.Equal(1.5, result.Iob);
        Assert.True(result.TreatmentIob.HasValue);
        Assert.Equal("Loop", result.Source);
    }

    #endregion

    #region CalcTempBasalIob Tests

    [Fact]
    public void CalcTempBasalIob_AboveScheduled_ShouldReturnPositiveIob()
    {
        var now = DateTimeOffset.UtcNow;
        var tempBasal = new TempBasal
        {
            StartTimestamp = now.AddMinutes(-30).UtcDateTime,
            EndTimestamp = now.AddMinutes(-1).UtcDateTime,
            Rate = 1.5,
            ScheduledRate = 0.5,
            Origin = TempBasalOrigin.Algorithm,
        };

        var result = _iobService.CalcTempBasalIob(tempBasal, now.ToUnixTimeMilliseconds(), _defaultSnapshot);

        Assert.True(result.IobContrib > 0);
    }

    [Fact]
    public void CalcTempBasalIob_AtScheduledRate_ShouldReturnZero()
    {
        var now = DateTimeOffset.UtcNow;
        var tempBasal = new TempBasal
        {
            StartTimestamp = now.AddMinutes(-30).UtcDateTime,
            EndTimestamp = now.AddMinutes(-1).UtcDateTime,
            Rate = 1.0,
            ScheduledRate = 1.0,
            Origin = TempBasalOrigin.Algorithm,
        };

        var result = _iobService.CalcTempBasalIob(tempBasal, now.ToUnixTimeMilliseconds(), _defaultSnapshot);

        Assert.Equal(0.0, result.IobContrib);
    }

    [Fact]
    public void CalcTempBasalIob_BelowScheduled_ShouldReturnZero()
    {
        var now = DateTimeOffset.UtcNow;
        var tempBasal = new TempBasal
        {
            StartTimestamp = now.AddMinutes(-30).UtcDateTime,
            EndTimestamp = now.AddMinutes(-1).UtcDateTime,
            Rate = 0.3,
            ScheduledRate = 1.0,
            Origin = TempBasalOrigin.Algorithm,
        };

        var result = _iobService.CalcTempBasalIob(tempBasal, now.ToUnixTimeMilliseconds(), _defaultSnapshot);

        Assert.Equal(0.0, result.IobContrib);
    }

    [Fact]
    public void CalcTempBasalIob_Suspended_ShouldReturnZero()
    {
        var now = DateTimeOffset.UtcNow;
        var tempBasal = new TempBasal
        {
            StartTimestamp = now.AddMinutes(-30).UtcDateTime,
            EndTimestamp = now.AddMinutes(-1).UtcDateTime,
            Rate = 1.5,
            ScheduledRate = 0.5,
            Origin = TempBasalOrigin.Suspended,
        };

        var result = _iobService.CalcTempBasalIob(tempBasal, now.ToUnixTimeMilliseconds(), _defaultSnapshot);

        Assert.Equal(0.0, result.IobContrib);
    }

    [Fact]
    public void CalcTempBasalIob_NoEndTime_ShouldReturnZero()
    {
        var now = DateTimeOffset.UtcNow;
        var tempBasal = new TempBasal
        {
            StartTimestamp = now.AddMinutes(-30).UtcDateTime,
            EndTimestamp = null,
            Rate = 2.0,
            ScheduledRate = 0.5,
            Origin = TempBasalOrigin.Algorithm,
        };

        var result = _iobService.CalcTempBasalIob(tempBasal, now.ToUnixTimeMilliseconds(), _defaultSnapshot);

        Assert.Equal(0.0, result.IobContrib);
    }

    [Fact]
    public void CalcTempBasalIob_AfterDIA_ShouldReturnZero()
    {
        var now = DateTimeOffset.UtcNow;
        var tempBasal = new TempBasal
        {
            StartTimestamp = now.AddHours(-4).UtcDateTime,
            EndTimestamp = now.AddHours(-3.5).UtcDateTime,
            Rate = 2.0,
            ScheduledRate = 0.5,
            Origin = TempBasalOrigin.Algorithm,
        };

        var result = _iobService.CalcTempBasalIob(tempBasal, now.ToUnixTimeMilliseconds(), _defaultSnapshot);

        Assert.Equal(0.0, result.IobContrib);
    }

    [Fact]
    public void CalcTempBasalIob_LinearDecay_ShouldDecreaseOverTime()
    {
        var now = DateTimeOffset.UtcNow;
        var tempBasal = new TempBasal
        {
            StartTimestamp = now.AddMinutes(-60).UtcDateTime,
            EndTimestamp = now.AddMinutes(-30).UtcDateTime,
            Rate = 2.0,
            ScheduledRate = 0.5,
            Origin = TempBasalOrigin.Algorithm,
        };

        var earlier = _iobService.CalcTempBasalIob(tempBasal, now.AddMinutes(-30).ToUnixTimeMilliseconds(), _defaultSnapshot);
        var later = _iobService.CalcTempBasalIob(tempBasal, now.ToUnixTimeMilliseconds(), _defaultSnapshot);

        Assert.True(earlier.IobContrib > later.IobContrib);
        Assert.True(later.IobContrib > 0);
    }

    #endregion

    #region FromTempBasals Tests

    [Fact]
    public void FromTempBasals_MultipleTempBasals_ShouldAggregateBasalIob()
    {
        var now = DateTimeOffset.UtcNow;
        var tempBasals = new List<TempBasal>
        {
            new()
            {
                StartTimestamp = now.AddMinutes(-60).UtcDateTime,
                EndTimestamp = now.AddMinutes(-30).UtcDateTime,
                Rate = 2.0, ScheduledRate = 0.5,
                Origin = TempBasalOrigin.Algorithm,
            },
            new()
            {
                StartTimestamp = now.AddMinutes(-30).UtcDateTime,
                EndTimestamp = now.AddMinutes(-5).UtcDateTime,
                Rate = 1.8, ScheduledRate = 0.5,
                Origin = TempBasalOrigin.Algorithm,
            },
        };

        var result = _iobService.FromTempBasals(tempBasals, now.ToUnixTimeMilliseconds(), _defaultSnapshot);

        Assert.True(result.BasalIob.HasValue);
        Assert.True(result.BasalIob!.Value > 0);
    }

    [Fact]
    public void FromTempBasals_EmptyList_ShouldReturnZero()
    {
        var result = _iobService.FromTempBasals(new List<TempBasal>(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _defaultSnapshot);

        Assert.Equal(0.0, result.Iob);
        Assert.Null(result.BasalIob);
    }

    [Fact]
    public void FromTempBasals_SetsBasalIobNotBolus()
    {
        var now = DateTimeOffset.UtcNow;
        var tempBasals = new List<TempBasal>
        {
            new()
            {
                StartTimestamp = now.AddMinutes(-30).UtcDateTime,
                EndTimestamp = now.AddMinutes(-5).UtcDateTime,
                Rate = 2.0, ScheduledRate = 0.5,
                Origin = TempBasalOrigin.Algorithm,
            },
        };

        var result = _iobService.FromTempBasals(tempBasals, now.ToUnixTimeMilliseconds(), _defaultSnapshot);

        Assert.Equal(0.0, result.Iob);
        Assert.True(result.BasalIob.HasValue);
        Assert.True(result.BasalIob!.Value > 0);
    }

    #endregion

    #region Per-Treatment Insulin Context Tests

    [Fact]
    public void CalcTreatment_WithInsulinContext_ShouldUseContextDia()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment
        {
            Mills = time - 1, Insulin = 1.0,
            InsulinContext = new TreatmentInsulinContext
            {
                PatientInsulinId = Guid.NewGuid(), InsulinName = "Fiasp",
                Dia = 5.0, Peak = 90, Curve = "rapid-acting", Concentration = 100,
            },
        };

        var result = _iobService.CalcTreatment(treatment, time + 3 * 60 * 60 * 1000, _defaultSnapshot);

        Assert.True(result.IobContrib > 0, "IOB should still be active at 3hrs with 5hr DIA");
    }

    [Fact]
    public void CalcTreatment_WithoutInsulinContext_ShouldUseProfileDia()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment { Mills = time - 1, Insulin = 1.0, InsulinContext = null };

        var result = _iobService.CalcTreatment(treatment, time + 3 * 60 * 60 * 1000, _defaultSnapshot);

        Assert.Equal(0.0, result.IobContrib, 3);
    }

    [Fact]
    public void CalcTreatment_WithInsulinContext_ShouldUseContextPeak()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatmentWithContext = new Treatment
        {
            Mills = time - 1, Insulin = 1.0,
            InsulinContext = new TreatmentInsulinContext
            {
                PatientInsulinId = Guid.NewGuid(), InsulinName = "Regular",
                Dia = 3.0, Peak = 120, Curve = "rapid-acting", Concentration = 100,
            },
        };
        var treatmentWithoutContext = new Treatment { Mills = time - 1, Insulin = 1.0, InsulinContext = null };

        var atTime = time + 80 * 60 * 1000;
        var resultWithContext = _iobService.CalcTreatment(treatmentWithContext, atTime, _defaultSnapshot);
        var resultWithoutContext = _iobService.CalcTreatment(treatmentWithoutContext, atTime, _defaultSnapshot);

        Assert.NotEqual(Math.Round(resultWithContext.IobContrib, 5), Math.Round(resultWithoutContext.IobContrib, 5));
    }

    [Fact]
    public void FromTreatments_MixedContextAndNoContext_ShouldUseRespectiveDia()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new()
            {
                Mills = time - 1, Insulin = 1.0,
                InsulinContext = new TreatmentInsulinContext
                {
                    PatientInsulinId = Guid.NewGuid(), InsulinName = "Fiasp",
                    Dia = 5.0, Peak = 90, Curve = "rapid-acting", Concentration = 100,
                },
            },
            new() { Mills = time - 1, Insulin = 1.0, InsulinContext = null },
        };

        var result = _iobService.FromTreatments(treatments, time + 3 * 60 * 60 * 1000, _defaultSnapshot);

        Assert.True(result.Iob > 0, "Should have IOB from the 5hr DIA treatment");
        Assert.True(result.Iob < 1.0, "Should be less than full dose since one treatment is fully decayed");
    }

    #endregion

    #region Exact Legacy Test Cases

    [Fact]
    public void IOB_ExactLegacyTestCase_100mgdl_1UnitIOB()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment> { new() { Mills = time, Insulin = 1.0 } };
        var snapshot = BuildSnapshot(DefaultDIA, sens: 50, DefaultBasalRate);
        var service = BuildService(snapshot);

        var result = service.FromTreatments(treatments, time, snapshot);

        Assert.Equal(1.0, result.Iob, 2);
        Assert.Equal(0.0, result.Activity ?? 0.0, 3);
    }

    [Fact]
    public void IOB_ExactPolynomialCurve_BeforePeak()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment { Mills = time - 30 * 60 * 1000, Insulin = 1.0 };

        var result = _iobService.CalcTreatment(treatment, time, _defaultSnapshot);

        var expectedIob = 1.0 * (1.0 - 0.001852 * 49.0 + 0.001852 * 7.0);
        Assert.Equal(expectedIob, result.IobContrib, 5);
    }

    [Fact]
    public void IOB_ExactPolynomialCurve_AfterPeak()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatment = new Treatment { Mills = time - 120 * 60 * 1000, Insulin = 1.0 };

        var result = _iobService.CalcTreatment(treatment, time, _defaultSnapshot);

        var expectedIob = 1.0 * (0.001323 * 81.0 - 0.054233 * 9.0 + 0.55556);
        Assert.Equal(expectedIob, result.IobContrib, 5);
    }

    #endregion
}
