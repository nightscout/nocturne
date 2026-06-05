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
/// Tests for the V4-native <see cref="IobCalculator"/> operating on <see cref="Bolus"/>
/// and <see cref="TempBasal"/> records. Verifies the same two-phase exponential decay curve
/// as <see cref="IobServiceTests"/> but via the <see cref="IIobCalculator"/> interface.
/// </summary>
public class IobCalculatorTests
{
    private readonly IobCalculator _calculator;
    private readonly Mock<IApsSnapshotRepository> _apsSnapshotRepo;
    private readonly Mock<IPumpSnapshotRepository> _pumpSnapshotRepo;
    private readonly Mock<IBasalRateResolver> _basalRateResolver;

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

        _basalRateResolver = new Mock<IBasalRateResolver>();
        _basalRateResolver
            .Setup(b => b.GetBasalRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultBasalRate);

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
            _basalRateResolver.Object,
            _apsSnapshotRepo.Object,
            _pumpSnapshotRepo.Object
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

    #region CalcTempBasal Tests

    [Fact]
    public void CalcTempBasal_WithInsulinContext_UsesDiaFromContext()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var oneHourAgo = now - 60 * 60 * 1000;
        var thirtyMinAgo = now - 30 * 60 * 1000;

        var tempBasal = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(oneHourAgo).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(thirtyMinAgo).UtcDateTime,
            Rate = 2.0,
            ScheduledRate = 1.0,
            Origin = TempBasalOrigin.Algorithm,
            InsulinContext = new TreatmentInsulinContext
            {
                Dia = 5.0,
                Peak = 55,
                Curve = "ultra-rapid",
                Concentration = 100,
            },
        };

        var resultWithContext = _calculator.CalcTempBasal(tempBasal, now);

        // Now remove InsulinContext so it falls back to profile DIA=3
        tempBasal.InsulinContext = null;
        var resultWithoutContext = _calculator.CalcTempBasal(tempBasal, now);

        // Longer DIA (5h) means slower decay, so MORE IOB remaining
        Assert.True(
            resultWithContext.IobContrib > resultWithoutContext.IobContrib,
            $"DIA=5h IOB ({resultWithContext.IobContrib}) should exceed DIA=3h IOB ({resultWithoutContext.IobContrib})");
    }

    [Fact]
    public void CalcTempBasal_WithoutInsulinContext_FallsBackToProfileDia()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var oneHourAgo = now - 60 * 60 * 1000;
        var thirtyMinAgo = now - 30 * 60 * 1000;

        var tempBasal = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(oneHourAgo).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(thirtyMinAgo).UtcDateTime,
            Rate = 2.0,
            ScheduledRate = 1.0,
            Origin = TempBasalOrigin.Algorithm,
            InsulinContext = null,
        };

        var result = _calculator.CalcTempBasal(tempBasal, now);

        Assert.True(result.IobContrib > 0, "TempBasal with rate > scheduled should have non-zero IOB");
    }

    #endregion

    #region TherapySnapshot overloads (async-free chart hot path)

    // A snapshot whose in-memory lookups return the same values as the mocked async resolvers
    // (DIA 3.0, sensitivity 95.0, basal rate 1.0), so the snapshot overloads must produce
    // numerically identical results to the legacy async-resolver path.
    private static TherapySnapshot MatchingSnapshot() => new(
        dia: DefaultDIA,
        peakMinutes: 75,
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
        _basalRateResolver.Verify(b => b.GetBasalRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void FromTempBasals_SnapshotOverload_MatchesAsyncPath()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Null ScheduledRate forces resolution — the erik case. Snapshot must supply it in-memory.
        var tempBasal = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 60 * 60 * 1000).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 30 * 60 * 1000).UtcDateTime,
            Rate = 2.0,
            ScheduledRate = null,
            Origin = TempBasalOrigin.Algorithm,
        };

        // viaAsync resolves the scheduled rate via the (mocked) async resolver; viaSnapshot reads
        // it from the in-memory snapshot. Both supply 1.0 here, so results must be identical.
        var viaAsync = _calculator.FromTempBasals([tempBasal], now);
        var viaSnapshot = _calculator.FromTempBasals([tempBasal], MatchingSnapshot(), now);

        Assert.Equal(viaAsync.BasalIob, viaSnapshot.BasalIob);
        Assert.NotNull(viaSnapshot.BasalIob);
        Assert.True(viaSnapshot.BasalIob!.Value > 0);
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
}
