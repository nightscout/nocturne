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

        var basalRateResolver = new Mock<IBasalRateResolver>();
        basalRateResolver
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
            basalRateResolver.Object,
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
