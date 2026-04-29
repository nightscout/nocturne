using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// Parity tests proving <see cref="IobCalculator"/> produces identical results to
/// the legacy IobService for the same inputs. Every assertion value is copied verbatim
/// from IobServiceTests — if a value differs, the math changed.
/// </summary>
public class IobCalculatorParityTests
{
    private readonly IobCalculator _calculator;
    private readonly Mock<IApsSnapshotRepository> _apsSnapshotRepo;
    private readonly Mock<IPumpSnapshotRepository> _pumpSnapshotRepo;

    private const double DefaultDIA = 3.0;
    private const double DefaultSensitivity = 95.0;
    private const double DefaultBasalRate = 1.0;

    public IobCalculatorParityTests()
    {
        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings
            .Setup(t => t.GetDIAAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultDIA);

        var sensitivityResolver = new Mock<ISensitivityResolver>();
        sensitivityResolver
            .Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultSensitivity);

        var basalRateResolver = new Mock<IBasalRateResolver>();
        basalRateResolver
            .Setup(b => b.GetBasalRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultBasalRate);

        _apsSnapshotRepo = new Mock<IApsSnapshotRepository>();
        _pumpSnapshotRepo = new Mock<IPumpSnapshotRepository>();

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());
        _pumpSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<PumpSnapshot>());

        _calculator = new IobCalculator(
            therapySettings.Object,
            sensitivityResolver.Object,
            basalRateResolver.Object,
            _apsSnapshotRepo.Object,
            _pumpSnapshotRepo.Object
        );
    }

    private static IobCalculator CreateCalculatorWithDIA(double dia)
    {
        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings
            .Setup(t => t.GetDIAAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dia);

        var sensitivityResolver = new Mock<ISensitivityResolver>();
        sensitivityResolver
            .Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultSensitivity);

        var basalRateResolver = new Mock<IBasalRateResolver>();
        basalRateResolver
            .Setup(b => b.GetBasalRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultBasalRate);

        var apsRepo = new Mock<IApsSnapshotRepository>();
        apsRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());
        var pumpRepo = new Mock<IPumpSnapshotRepository>();
        pumpRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<PumpSnapshot>());

        return new IobCalculator(therapySettings.Object, sensitivityResolver.Object, basalRateResolver.Object, apsRepo.Object, pumpRepo.Object);
    }

    private static IobCalculator CreateCalculatorWithSensitivity(double sens)
    {
        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings
            .Setup(t => t.GetDIAAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultDIA);

        var sensitivityResolver = new Mock<ISensitivityResolver>();
        sensitivityResolver
            .Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sens);

        var basalRateResolver = new Mock<IBasalRateResolver>();
        basalRateResolver
            .Setup(b => b.GetBasalRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultBasalRate);

        var apsRepo = new Mock<IApsSnapshotRepository>();
        apsRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());
        var pumpRepo = new Mock<IPumpSnapshotRepository>();
        pumpRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<PumpSnapshot>());

        return new IobCalculator(therapySettings.Object, sensitivityResolver.Object, basalRateResolver.Object, apsRepo.Object, pumpRepo.Object);
    }

    private static Bolus MakeBolus(long mills, double insulin, TreatmentInsulinContext? insulinContext = null)
    {
        return new Bolus
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime,
            Insulin = insulin,
            InsulinContext = insulinContext,
        };
    }

    #region CalcBolus Parity (mirrors CalcTreatment tests)

    [Fact]
    public void CalcBolus_SingleBolusRightAfter_ShouldReturn1Point00IOB()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bolus = MakeBolus(time - 1, 1.0);

        var result = _calculator.CalcBolus(bolus, time);

        Assert.Equal(1.0, result.IobContrib, 2);
    }

    [Fact]
    public void CalcBolus_After1Hour_ShouldHaveLessIOBThan1()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bolus = MakeBolus(time - 1, 1.0);

        var result = _calculator.CalcBolus(bolus, time + 60 * 60 * 1000);

        Assert.True(result.IobContrib < 1.0);
        Assert.True(result.IobContrib > 0.0);
    }

    [Fact]
    public void CalcBolus_After3Hours_ShouldHaveZeroIOB()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bolus = MakeBolus(time - 1, 1.0);

        var result = _calculator.CalcBolus(bolus, time + 3 * 60 * 60 * 1000);

        Assert.Equal(0.0, result.IobContrib, 3);
    }

    [Fact]
    public void CalcBolus_NoNegativeIOB_WhenApproachingZero()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bolus = MakeBolus(time, 5.0);

        var result = _calculator.CalcBolus(bolus, time + 3 * 60 * 60 * 1000 - 90 * 1000);

        Assert.True(result.IobContrib >= 0.0);
    }

    [Fact]
    public void CalcBolus_4HourDIA_ShouldUseCorrectDuration()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var calculator = CreateCalculatorWithDIA(4.0);
        var bolus = MakeBolus(time - 1, 1.0);

        var rightAfter = calculator.CalcBolus(bolus, time);
        var afterHour = calculator.CalcBolus(bolus, time + 60 * 60 * 1000);

        Assert.Equal(1.0, rightAfter.IobContrib, 2);
        Assert.True(afterHour.IobContrib > 0.5);
    }

    #endregion

    #region FromBoluses Parity (mirrors FromTreatments tests)

    [Fact]
    public void FromBoluses_MultipleBoluses_ShouldAggregateCorrectly()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var boluses = new List<Bolus>
        {
            MakeBolus(time - 60 * 60 * 1000, 2.0),
            MakeBolus(time - 30 * 60 * 1000, 1.5),
            MakeBolus(time - 10 * 60 * 1000, 1.0),
        };

        var result = _calculator.FromBoluses(boluses, time);

        Assert.True(result.Iob > 0);
        Assert.True(result.Iob < 4.5);
        Assert.Equal("Care Portal", result.Source);
    }

    #endregion

    #region ApsSnapshot IOB Priority Tests

    [Fact]
    public async Task CalculateTotal_UsesApsSnapshotIob_WhenAvailable()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var boluses = new List<Bolus>
        {
            MakeBolus(time - 60 * 60 * 1000, 1.0),
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

        var result = await _calculator.CalculateTotalAsync(boluses, time: time);

        Assert.Equal(1.5, result.Iob);
        Assert.Equal("Loop", result.Source);
        Assert.True(result.TreatmentIob.HasValue);
    }

    [Fact]
    public async Task CalculateTotal_UsesOpenApsSnapshot_WhenAvailable()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var boluses = new List<Bolus>
        {
            MakeBolus(time - 60 * 60 * 1000, 1.0),
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

        var result = await _calculator.CalculateTotalAsync(boluses, time: time);

        Assert.Equal(0.5, result.Iob);
        Assert.Equal("OpenAPS", result.Source);
    }

    [Fact]
    public async Task CalculateTotal_UsesAapsSnapshot_WhenAvailable()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var boluses = new List<Bolus>
        {
            MakeBolus(time - 60 * 60 * 1000, 1.0),
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

        var result = await _calculator.CalculateTotalAsync(boluses, time: time);

        Assert.Equal(0.8, result.Iob);
        Assert.Equal("OpenAPS", result.Source);
    }

    [Fact]
    public async Task CalculateTotal_StaleApsSnapshot_FallsThroughToBoluses()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var boluses = new List<Bolus>
        {
            MakeBolus(time - 30 * 60 * 1000, 2.0),
        };

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());

        var result = await _calculator.CalculateTotalAsync(boluses, time: time);

        Assert.True(result.Iob > 0);
        Assert.Equal("Care Portal", result.Source);
    }

    #endregion

    #region PumpSnapshot IOB Tests

    [Fact]
    public async Task CalculateTotal_UsesPumpSnapshotIob_WhenNoApsSnapshot()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var boluses = new List<Bolus>
        {
            MakeBolus(time - 60 * 60 * 1000, 1.0),
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

        var result = await _calculator.CalculateTotalAsync(boluses, time: time);

        Assert.Equal(0.87, result.Iob);
        Assert.Equal("Pump", result.Source);
    }

    [Fact]
    public async Task CalculateTotal_PumpSnapshotUsesBolusIob_WhenIobIsNull()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var boluses = new List<Bolus>();

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

        var result = await _calculator.CalculateTotalAsync(boluses, time: time);

        Assert.Equal(1.23, result.Iob);
        Assert.Equal("Pump", result.Source);
    }

    [Fact]
    public async Task CalculateTotal_ApsSnapshotTakesPriority_OverPumpSnapshot()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var boluses = new List<Bolus>();

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

        var result = await _calculator.CalculateTotalAsync(boluses, time: time);

        Assert.Equal(1.5, result.Iob);
        Assert.Equal("Loop", result.Source);
    }

    #endregion

    #region CalculateTotal Fallback Tests

    [Fact]
    public async Task CalculateTotal_FallsBackToBoluses_WhenNoSnapshots()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var boluses = new List<Bolus>
        {
            MakeBolus(time - 30 * 60 * 1000, 2.0),
        };

        var result = await _calculator.CalculateTotalAsync(boluses, time: time);

        Assert.True(result.Iob > 0);
        Assert.Equal("Care Portal", result.Source);
    }

    [Fact]
    public async Task CalculateTotal_CombinesDeviceIobAndBolusIob()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var boluses = new List<Bolus>
        {
            MakeBolus(time - 60 * 60 * 1000, 1.0),
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

        var result = await _calculator.CalculateTotalAsync(boluses, time: time);

        Assert.Equal(1.5, result.Iob);
        Assert.True(result.TreatmentIob.HasValue);
        Assert.Equal("Loop", result.Source);
    }

    #endregion

    #region CalcTempBasal Parity (mirrors CalcTempBasalIob tests)

    [Fact]
    public void CalcTempBasal_AboveScheduled_ShouldReturnPositiveIob()
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

        var result = _calculator.CalcTempBasal(tempBasal, now.ToUnixTimeMilliseconds());

        Assert.True(result.IobContrib > 0);
    }

    [Fact]
    public void CalcTempBasal_AtScheduledRate_ShouldReturnZero()
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

        var result = _calculator.CalcTempBasal(tempBasal, now.ToUnixTimeMilliseconds());

        Assert.Equal(0.0, result.IobContrib);
    }

    [Fact]
    public void CalcTempBasal_BelowScheduled_ShouldReturnZero()
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

        var result = _calculator.CalcTempBasal(tempBasal, now.ToUnixTimeMilliseconds());

        Assert.Equal(0.0, result.IobContrib);
    }

    [Fact]
    public void CalcTempBasal_Suspended_ShouldReturnZero()
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

        var result = _calculator.CalcTempBasal(tempBasal, now.ToUnixTimeMilliseconds());

        Assert.Equal(0.0, result.IobContrib);
    }

    [Fact]
    public void CalcTempBasal_NoEndTime_ShouldReturnZero()
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

        var result = _calculator.CalcTempBasal(tempBasal, now.ToUnixTimeMilliseconds());

        Assert.Equal(0.0, result.IobContrib);
    }

    [Fact]
    public void CalcTempBasal_AfterDIA_ShouldReturnZero()
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

        var result = _calculator.CalcTempBasal(tempBasal, now.ToUnixTimeMilliseconds());

        Assert.Equal(0.0, result.IobContrib);
    }

    [Fact]
    public void CalcTempBasal_LinearDecay_ShouldDecreaseOverTime()
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

        var earlier = _calculator.CalcTempBasal(tempBasal, now.AddMinutes(-30).ToUnixTimeMilliseconds());
        var later = _calculator.CalcTempBasal(tempBasal, now.ToUnixTimeMilliseconds());

        Assert.True(earlier.IobContrib > later.IobContrib);
        Assert.True(later.IobContrib > 0);
    }

    #endregion

    #region FromTempBasals Parity

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

        var result = _calculator.FromTempBasals(tempBasals, now.ToUnixTimeMilliseconds());

        Assert.True(result.BasalIob.HasValue);
        Assert.True(result.BasalIob!.Value > 0);
    }

    [Fact]
    public void FromTempBasals_EmptyList_ShouldReturnZero()
    {
        var result = _calculator.FromTempBasals(new List<TempBasal>(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

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

        var result = _calculator.FromTempBasals(tempBasals, now.ToUnixTimeMilliseconds());

        Assert.Equal(0.0, result.Iob);
        Assert.True(result.BasalIob.HasValue);
        Assert.True(result.BasalIob!.Value > 0);
    }

    #endregion

    #region Per-Bolus Insulin Context Tests

    [Fact]
    public void CalcBolus_WithInsulinContext_ShouldUseContextDia()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bolus = MakeBolus(time - 1, 1.0, new TreatmentInsulinContext
        {
            PatientInsulinId = Guid.NewGuid(), InsulinName = "Fiasp",
            Dia = 5.0, Peak = 90, Curve = "rapid-acting", Concentration = 100,
        });

        var result = _calculator.CalcBolus(bolus, time + 3 * 60 * 60 * 1000);

        Assert.True(result.IobContrib > 0, "IOB should still be active at 3hrs with 5hr DIA");
    }

    [Fact]
    public void CalcBolus_WithoutInsulinContext_ShouldUseProfileDia()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bolus = MakeBolus(time - 1, 1.0);

        var result = _calculator.CalcBolus(bolus, time + 3 * 60 * 60 * 1000);

        Assert.Equal(0.0, result.IobContrib, 3);
    }

    [Fact]
    public void CalcBolus_WithInsulinContext_ShouldUseContextPeak()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bolusWithContext = MakeBolus(time - 1, 1.0, new TreatmentInsulinContext
        {
            PatientInsulinId = Guid.NewGuid(), InsulinName = "Regular",
            Dia = 3.0, Peak = 120, Curve = "rapid-acting", Concentration = 100,
        });
        var bolusWithoutContext = MakeBolus(time - 1, 1.0);

        var atTime = time + 80 * 60 * 1000;
        var resultWithContext = _calculator.CalcBolus(bolusWithContext, atTime);
        var resultWithoutContext = _calculator.CalcBolus(bolusWithoutContext, atTime);

        Assert.NotEqual(Math.Round(resultWithContext.IobContrib, 5), Math.Round(resultWithoutContext.IobContrib, 5));
    }

    [Fact]
    public void FromBoluses_MixedContextAndNoContext_ShouldUseRespectiveDia()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var boluses = new List<Bolus>
        {
            MakeBolus(time - 1, 1.0, new TreatmentInsulinContext
            {
                PatientInsulinId = Guid.NewGuid(), InsulinName = "Fiasp",
                Dia = 5.0, Peak = 90, Curve = "rapid-acting", Concentration = 100,
            }),
            MakeBolus(time - 1, 1.0),
        };

        var result = _calculator.FromBoluses(boluses, time + 3 * 60 * 60 * 1000);

        Assert.True(result.Iob > 0, "Should have IOB from the 5hr DIA bolus");
        Assert.True(result.Iob < 1.0, "Should be less than full dose since one bolus is fully decayed");
    }

    #endregion

    #region Exact Legacy Test Cases

    [Fact]
    public void IOB_ExactLegacyTestCase_100mgdl_1UnitIOB()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var boluses = new List<Bolus> { MakeBolus(time, 1.0) };
        var calculator = CreateCalculatorWithSensitivity(50);

        var result = calculator.FromBoluses(boluses, time);

        Assert.Equal(1.0, result.Iob, 2);
        Assert.Equal(0.0, result.Activity ?? 0.0, 3);
    }

    [Fact]
    public void IOB_ExactPolynomialCurve_BeforePeak()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bolus = MakeBolus(time - 30 * 60 * 1000, 1.0);

        var result = _calculator.CalcBolus(bolus, time);

        var expectedIob = 1.0 * (1.0 - 0.001852 * 49.0 + 0.001852 * 7.0);
        Assert.Equal(expectedIob, result.IobContrib, 5);
    }

    [Fact]
    public void IOB_ExactPolynomialCurve_AfterPeak()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bolus = MakeBolus(time - 120 * 60 * 1000, 1.0);

        var result = _calculator.CalcBolus(bolus, time);

        var expectedIob = 1.0 * (0.001323 * 81.0 - 0.054233 * 9.0 + 0.55556);
        Assert.Equal(expectedIob, result.IobContrib, 5);
    }

    #endregion
}
