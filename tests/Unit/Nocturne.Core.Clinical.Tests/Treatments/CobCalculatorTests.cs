using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Clinical.Tests.Treatments;

/// <summary>
/// Tests for the V4-native <see cref="CobCalculator"/> operating on <see cref="CarbIntake"/>
/// and <see cref="Bolus"/> records. Verifies the same decay algorithm as <see cref="CobServiceTests"/>
/// but via the <see cref="ICobCalculator"/> interface, without legacy absorption adjustments.
/// </summary>
public class CobCalculatorTests
{
    private readonly CobCalculator _calculator;
    private readonly Mock<IApsSnapshotRepository> _apsSnapshotRepo;

    private const double DefaultCarbAbsorptionRate = 30.0;
    private const double DefaultSensitivity = 50.0;
    private const double DefaultCarbRatio = 18.0;
    private const double DefaultDIA = 3.0;
    private const double DefaultBasalRate = 1.0;

    public CobCalculatorTests()
    {
        var logger = new Mock<ILogger<CobCalculator>>();

        var sensitivityResolver = new Mock<ISensitivityResolver>();
        sensitivityResolver
            .Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultSensitivity);

        var carbRatioResolver = new Mock<ICarbRatioResolver>();
        carbRatioResolver
            .Setup(c => c.GetCarbRatioAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultCarbRatio);

        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings
            .Setup(t => t.GetCarbAbsorptionRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultCarbAbsorptionRate);
        therapySettings
            .Setup(t => t.GetDIAAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultDIA);

        _apsSnapshotRepo = new Mock<IApsSnapshotRepository>();
        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());

        // Real IobCalculator — COB calculation calls IOB internally for activity
        var basalSegments = MockBasalSegments.Flat(DefaultBasalRate);

        var iobApsRepo = new Mock<IApsSnapshotRepository>();
        iobApsRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());

        var pumpRepo = new Mock<IPumpSnapshotRepository>();
        pumpRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<PumpSnapshot>());

        var iobCalculator = new IobCalculator(
            therapySettings.Object,
            sensitivityResolver.Object,
            basalSegments.Object,
            iobApsRepo.Object,
            pumpRepo.Object,
            MockStateSpans.Empty().Object
        );

        _calculator = new CobCalculator(
            logger.Object,
            iobCalculator,
            sensitivityResolver.Object,
            carbRatioResolver.Object,
            therapySettings.Object,
            _apsSnapshotRepo.Object
        );
    }

    #region TherapySnapshot overload (async-free chart hot path)

    // Snapshot whose in-memory lookups equal the mocked resolvers (sens 50, carb ratio 18,
    // carb absorption 30, DIA 3, basal 1.0). With no device snapshots present, the snapshot
    // overload must reproduce the async path's COB exactly.
    private static TherapySnapshot MatchingSnapshot() => new(
        dia: DefaultDIA,
        carbsPerHour: DefaultCarbAbsorptionRate,
        timezone: null,
        ccpPercentage: null,
        ccpTimeshiftMs: 0,
        sensitivityEntries: new[] { new ScheduleEntry { TimeAsSeconds = 0, Value = DefaultSensitivity } },
        carbRatioEntries: new[] { new ScheduleEntry { TimeAsSeconds = 0, Value = DefaultCarbRatio } },
        basalEntries: new[] { new ScheduleEntry { TimeAsSeconds = 0, Value = DefaultBasalRate } });

    [Fact]
    public async Task FromCarbIntakes_SnapshotOverload_MatchesAsyncPath_WhenNoDeviceSnapshots()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var carbs = new List<CarbIntake>
        {
            new() { Carbs = 50, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 45 * 60 * 1000).UtcDateTime },
            new() { Carbs = 20, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 100 * 60 * 1000).UtcDateTime },
        };
        var boluses = new List<Bolus>
        {
            new() { Insulin = 3.0, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 60 * 60 * 1000).UtcDateTime },
            new() { Insulin = 1.5, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 20 * 60 * 1000).UtcDateTime },
        };
        var tempBasals = new List<TempBasal>
        {
            new()
            {
                StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 50 * 60 * 1000).UtcDateTime,
                EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 20 * 60 * 1000).UtcDateTime,
                Rate = 1.8,
                ScheduledRate = null,
                Origin = TempBasalOrigin.Algorithm,
            },
        };

        var viaAsync = await _calculator.FromCarbIntakesAsync(carbs, boluses, tempBasals, now);
        var viaSnapshot = _calculator.FromCarbIntakes(carbs, boluses, tempBasals, MatchingSnapshot(), (_, _) => DefaultBasalRate, now);

        Assert.Equal(viaAsync.Cob, viaSnapshot.Cob, 6);
        Assert.Equal(viaAsync.DecayedBy, viaSnapshot.DecayedBy);
        Assert.Equal(viaAsync.IsDecaying, viaSnapshot.IsDecaying);
        Assert.Equal(viaAsync.RawCarbImpact.GetValueOrDefault(), viaSnapshot.RawCarbImpact.GetValueOrDefault(), 6);
        Assert.True(viaSnapshot.Cob > 0, "sanity: the scenario should produce non-zero COB");
    }

    [Fact]
    public async Task FromCarbIntakes_RunningTemp_DelaysCarbsOnlyByWhatItDeliveredByNow()
    {
        // The carb delay reads insulin activity at the future decay time. A temp still running must
        // count only what it delivered by now, exactly as if it had ended now. The snapshot path takes
        // pieces, which always carry an end, so a temp due to end later stands in for a running one.
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var carbs = new List<CarbIntake>
        {
            new() { Carbs = 50, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 45 * 60 * 1000).UtcDateTime },
        };
        List<Bolus> boluses =
        [
            new() { Insulin = 1.0, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 45 * 60 * 1000).UtcDateTime },
        ];
        TempBasal HighTemp(long? end) => new()
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 30 * 60 * 1000).UtcDateTime,
            EndTimestamp = end is { } e ? DateTimeOffset.FromUnixTimeMilliseconds(e).UtcDateTime : null,
            Rate = 10.0,
            Origin = TempBasalOrigin.Algorithm,
        };

        var runningSnapshot = _calculator.FromCarbIntakes(carbs, boluses, [HighTemp(now + 30 * 60 * 1000)], MatchingSnapshot(), (_, _) => DefaultBasalRate, now);
        var endedSnapshot = _calculator.FromCarbIntakes(carbs, boluses, [HighTemp(now)], MatchingSnapshot(), (_, _) => DefaultBasalRate, now);
        var runningAsync = await _calculator.FromCarbIntakesAsync(carbs, boluses, [HighTemp(null)], now);
        var endedAsync = await _calculator.FromCarbIntakesAsync(carbs, boluses, [HighTemp(now)], now);
        var withoutTemp = await _calculator.FromCarbIntakesAsync(carbs, boluses, [], now);

        Assert.Equal(endedSnapshot.DecayedBy, runningSnapshot.DecayedBy);
        Assert.Equal(endedAsync.DecayedBy, runningAsync.DecayedBy);
        Assert.True(endedAsync.DecayedBy > withoutTemp.DecayedBy, "the temp's insulin must reach the carb delay");
        Assert.True(endedSnapshot.DecayedBy > withoutTemp.DecayedBy, "the temp's insulin must reach the carb delay");
    }

    [Fact]
    public async Task CalculateTotal_WithoutCarbs_MakesNoResolverRead()
    {
        var sensitivityResolver = new Mock<ISensitivityResolver>(MockBehavior.Strict);
        var carbRatioResolver = new Mock<ICarbRatioResolver>(MockBehavior.Strict);
        var therapySettings = new Mock<ITherapySettingsResolver>(MockBehavior.Strict);
        var apsSnapshotRepo = new Mock<IApsSnapshotRepository>();
        apsSnapshotRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var iobCalculator = new IobCalculator(
            therapySettings.Object,
            sensitivityResolver.Object,
            Mock.Of<IBasalSegmentService>(MockBehavior.Strict),
            Mock.Of<IApsSnapshotRepository>(MockBehavior.Strict),
            Mock.Of<IPumpSnapshotRepository>(MockBehavior.Strict),
            Mock.Of<IStateSpanRepository>(MockBehavior.Strict));
        var calculator = new CobCalculator(
            Mock.Of<ILogger<CobCalculator>>(),
            iobCalculator,
            sensitivityResolver.Object,
            carbRatioResolver.Object,
            therapySettings.Object,
            apsSnapshotRepo.Object);

        var result = await calculator.CalculateTotalAsync([], null, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Equal(0, result.Cob);
        therapySettings.VerifyNoOtherCalls();
        sensitivityResolver.VerifyNoOtherCalls();
        carbRatioResolver.VerifyNoOtherCalls();
    }

    [Fact]
    public void FromCarbIntakes_SnapshotOverload_MakesNoResolverCall()
    {
        var sensitivityResolver = new Mock<ISensitivityResolver>(MockBehavior.Strict);
        var carbRatioResolver = new Mock<ICarbRatioResolver>(MockBehavior.Strict);
        var therapySettings = new Mock<ITherapySettingsResolver>(MockBehavior.Strict);
        var iobCalculator = new IobCalculator(
            therapySettings.Object,
            sensitivityResolver.Object,
            Mock.Of<IBasalSegmentService>(MockBehavior.Strict),
            Mock.Of<IApsSnapshotRepository>(MockBehavior.Strict),
            Mock.Of<IPumpSnapshotRepository>(MockBehavior.Strict),
            Mock.Of<IStateSpanRepository>(MockBehavior.Strict));
        var calculator = new CobCalculator(
            Mock.Of<ILogger<CobCalculator>>(),
            iobCalculator,
            sensitivityResolver.Object,
            carbRatioResolver.Object,
            therapySettings.Object,
            Mock.Of<IApsSnapshotRepository>(MockBehavior.Strict));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var carbs = new List<CarbIntake>
        {
            new() { Carbs = 40, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 30 * 60 * 1000).UtcDateTime },
            new() { Carbs = 20, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 10 * 60 * 1000).UtcDateTime },
        };
        var boluses = new List<Bolus>
        {
            new() { Insulin = 2.0, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 25 * 60 * 1000).UtcDateTime },
        };

        var result = calculator.FromCarbIntakes(carbs, boluses, [], MatchingSnapshot(), (_, _) => DefaultBasalRate, now);

        Assert.True(result.Cob > 0);
        therapySettings.VerifyNoOtherCalls();
        sensitivityResolver.VerifyNoOtherCalls();
        carbRatioResolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FromCarbIntakes_FirstCarbWithNoBoluses_AsyncAndSnapshotAgree()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var carbs = new List<CarbIntake>
        {
            new() { Carbs = 50, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 30 * 60 * 1000).UtcDateTime },
        };

        var viaAsync = await _calculator.FromCarbIntakesAsync(carbs, null, null, now);
        var viaSnapshot = _calculator.FromCarbIntakes(carbs, null, null, MatchingSnapshot(), (_, _) => DefaultBasalRate, now);

        Assert.Equal(viaAsync.Cob, viaSnapshot.Cob, 6);
        Assert.Equal(viaAsync.DecayedBy, viaSnapshot.DecayedBy);
        Assert.True(viaSnapshot.Cob > 0);
    }

    [Fact]
    public async Task FromCarbIntakes_MissingInsulinActivity_DelaysCarbsAsZeroActivityDoes()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var carbs = new List<CarbIntake>
        {
            new() { Carbs = 30, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 180 * 60 * 1000).UtcDateTime },
            new() { Carbs = 30, Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 30 * 60 * 1000).UtcDateTime },
        };

        CobCalculator WithActivity(Func<long, double?> activityAt)
        {
            var iob = new Mock<IIobCalculator>();
            iob.Setup(i => i.CalculateTotalAsync(
                    It.IsAny<List<Bolus>>(), It.IsAny<List<TempBasal>?>(), It.IsAny<long>(),
                    It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((List<Bolus> _, List<TempBasal>? _, long t, long? _, bool _, CancellationToken _) =>
                    new IobResult { Activity = activityAt(t) });
            return new CobCalculator(
                Mock.Of<ILogger<CobCalculator>>(),
                iob.Object,
                Mock.Of<ISensitivityResolver>(r => r.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()) == Task.FromResult(DefaultSensitivity)),
                Mock.Of<ICarbRatioResolver>(r => r.GetCarbRatioAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()) == Task.FromResult(DefaultCarbRatio)),
                Mock.Of<ITherapySettingsResolver>(r => r.GetCarbAbsorptionRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()) == Task.FromResult(DefaultCarbAbsorptionRate)),
                Mock.Of<IApsSnapshotRepository>());
        }

        var missing = await WithActivity(t => t > now ? null : 1.0).FromCarbIntakesAsync(carbs, null, null, now);
        var zero = await WithActivity(t => t > now ? 0.0 : 1.0).FromCarbIntakesAsync(carbs, null, null, now);
        var none = await WithActivity(_ => 0.0).FromCarbIntakesAsync(carbs, null, null, now);

        Assert.Equal(zero.DecayedBy, missing.DecayedBy);
        Assert.Equal(zero.Cob, missing.Cob, 6);
        Assert.True(missing.DecayedBy > none.DecayedBy, "sanity: the earlier activity must delay the carbs");
    }

    #endregion

    #region CalculateTotalAsync Tests

    [Fact]
    public async Task CalculateTotal_RecentCarbs_ShouldReturnNonZeroCob()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var carbIntakes = new List<CarbIntake>
        {
            new()
            {
                Carbs = 50,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 30 * 60 * 1000).UtcDateTime,
            },
        };

        var result = await _calculator.CalculateTotalAsync(carbIntakes, null, null, now);

        Assert.True(result.Cob > 0, "50g carbs 30 min ago should have non-zero COB");
        Assert.True(result.Cob <= 50, "COB should not exceed total carbs");
    }

    [Fact]
    public async Task CalculateTotal_FullyAbsorbedCarbs_ShouldReturnZeroCob()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var carbIntakes = new List<CarbIntake>
        {
            new()
            {
                Carbs = 10,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 8 * 60 * 60 * 1000).UtcDateTime,
            },
        };

        var result = await _calculator.CalculateTotalAsync(carbIntakes, null, null, now);

        Assert.Equal(0.0, result.Cob);
    }

    [Fact]
    public async Task CalculateTotal_CustomAbsorptionTime_UsesOverride()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var fastIntake = new CarbIntake
        {
            Carbs = 30,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 90 * 60 * 1000).UtcDateTime,
            AbsorptionTime = 60,
        };
        var slowIntake = new CarbIntake
        {
            Carbs = 30,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 90 * 60 * 1000).UtcDateTime,
            AbsorptionTime = 240,
        };

        var fastResult = await _calculator.CalculateTotalAsync(new List<CarbIntake> { fastIntake }, null, null, now);
        var slowResult = await _calculator.CalculateTotalAsync(new List<CarbIntake> { slowIntake }, null, null, now);

        Assert.True(slowResult.Cob > fastResult.Cob,
            "Slow absorption (240 min) should have more COB remaining than fast absorption (60 min)");
    }

    #endregion

    #region FromCarbIntakes Tests

    [Fact]
    public async Task FromCarbIntakes_MultipleCarbIntakes_AggregatesCob()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var carbIntakes = new List<CarbIntake>
        {
            new()
            {
                Carbs = 30,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 30 * 60 * 1000).UtcDateTime,
            },
            new()
            {
                Carbs = 20,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 15 * 60 * 1000).UtcDateTime,
            },
        };

        var result = await _calculator.FromCarbIntakesAsync(carbIntakes, null, null, now);

        Assert.True(result.Cob > 0, "Multiple recent carb intakes should produce non-zero COB");
        Assert.True(result.Cob <= 50, "COB should not exceed total carbs (30 + 20)");
    }

    [Fact]
    public async Task FromCarbIntakes_TracksLastCarbs()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var olderIntake = new CarbIntake
        {
            Carbs = 30,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 60 * 60 * 1000).UtcDateTime,
        };
        var newerIntake = new CarbIntake
        {
            Carbs = 20,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 15 * 60 * 1000).UtcDateTime,
        };

        var result = await _calculator.FromCarbIntakesAsync(
            new List<CarbIntake> { olderIntake, newerIntake },
            null,
            null,
            now
        );

        Assert.NotNull(result.LastCarbs);
        Assert.Equal(20, result.LastCarbs!.Carbs);
    }

    #endregion

    #region CalculateTotalAsync device-value preference

    [Fact]
    public async Task CalculateTotalAsync_DeviceReportsZeroCob_ZeroIsAccepted()
    {
        // COB of exactly zero from the device means "no carbs on board" and must win over a
        // local recomputation that still sees recent carb treatments decaying.
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ApsSnapshot
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 2 * 60 * 1000).UtcDateTime,
                    Cob = 0.0,
                },
            });

        var carbIntakes = new List<CarbIntake>
        {
            new()
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(now - 30 * 60 * 1000).UtcDateTime,
                Carbs = 60.0,
            },
        };

        var result = await _calculator.CalculateTotalAsync(carbIntakes, null, null, now);

        Assert.Equal(0.0, result.Cob);
        Assert.NotEqual("Care Portal", result.Source);
    }

    [Fact]
    public async Task CalculateTotalAsync_HistoricalTime_DeviceAgeMeasuredAgainstRequestedInstant()
    {
        // A query for a past instant must judge snapshot freshness against that instant, not the
        // wall clock — otherwise every historical snapshot looks stale and is silently discarded.
        var historical = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ApsSnapshot
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(historical - 5 * 60 * 1000).UtcDateTime,
                    Cob = 25.0,
                },
            });

        var result = await _calculator.CalculateTotalAsync(new List<CarbIntake>(), null, null, historical);

        Assert.Equal(25.0, result.Cob);
    }

    #endregion
}
