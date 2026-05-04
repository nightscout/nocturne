using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Oref;
using OrefModels = Nocturne.Core.Oref.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// Complete COB calculation tests with 1:1 legacy JavaScript compatibility
/// Tests exact algorithms from ClientApp/mocha-tests/cob.test.js
/// NO SIMPLIFICATIONS - Must match legacy behavior exactly
/// </summary>
[Parity("cob.test.js")]
public class CobServiceTests
{
    private readonly ICobService _cobService;
    private readonly Mock<IApsSnapshotRepository> _apsSnapshotRepo;

    // Default test profile values
    private const double DefaultCarbAbsorptionRate = 30.0;
    private const double DefaultSensitivity = 50.0;
    private const double DefaultCarbRatio = 18.0;
    private const double DefaultDIA = 3.0;
    private const double DefaultBasalRate = 1.0;

    public CobServiceTests()
    {
        var logger = new Mock<ILogger<Nocturne.API.Services.Treatments.CobService>>();
        IIobService iobService = OrefService.IsAvailable() ? new OrefIobAdapter() : CreateDefaultIobService();

        var sensitivity = new Mock<ISensitivityResolver>();
        sensitivity.Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(DefaultSensitivity);

        var carbRatio = new Mock<ICarbRatioResolver>();
        carbRatio.Setup(c => c.GetCarbRatioAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(DefaultCarbRatio);

        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        therapySettings.Setup(t => t.GetCarbAbsorptionRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(DefaultCarbAbsorptionRate);

        _apsSnapshotRepo = new Mock<IApsSnapshotRepository>();
        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());

        var timelineResolver = new Mock<ITherapyTimelineResolver>();
        var snapshot = new TherapySnapshot(
            dia: DefaultDIA,
            peakMinutes: TherapySnapshot.DefaultPeakMinutes,
            carbsPerHour: DefaultCarbAbsorptionRate,
            timezone: null,
            ccpPercentage: null,
            ccpTimeshiftMs: 0,
            sensitivityEntries: new[] { new ScheduleEntry { Value = DefaultSensitivity, TimeAsSeconds = 0 } },
            carbRatioEntries: new[] { new ScheduleEntry { Value = DefaultCarbRatio, TimeAsSeconds = 0 } },
            basalEntries: new[] { new ScheduleEntry { Value = DefaultBasalRate, TimeAsSeconds = 0 } }
        );
        timelineResolver
            .Setup(r => r.GetSnapshotAtAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);

        _cobService = new Nocturne.API.Services.Treatments.CobService(
            logger.Object, iobService, _apsSnapshotRepo.Object, timelineResolver.Object);
    }

    [Fact]
    public async Task CobTotal_MultipleTreatments_ShouldMatchLegacyResults()
    {
        var treatments = new List<Treatment>
        {
            new()
            {
                Carbs = 100,
                Mills = new DateTimeOffset(2015, 5, 29, 2, 3, 48, 827, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            },
            new()
            {
                Carbs = 10,
                Mills = new DateTimeOffset(2015, 5, 29, 3, 45, 10, 670, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            },
        };

        var after100Time = new DateTimeOffset(2015, 5, 29, 2, 3, 49, 827, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var before10Time = new DateTimeOffset(2015, 5, 29, 3, 45, 10, 670, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var after10Time = new DateTimeOffset(2015, 5, 29, 3, 45, 11, 670, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var after100 = await _cobService.CobTotalAsync(treatments, after100Time);
        var before10 = await _cobService.CobTotalAsync(treatments, before10Time);
        var after10 = await _cobService.CobTotalAsync(treatments, after10Time);

        Assert.Equal(100.0, after100.Cob, 1);
        Assert.Equal(59.0, Math.Round(before10.Cob), 0);
        Assert.Equal(69.0, Math.Round(after10.Cob), 0);
    }

    [Fact]
    public async Task CobTotal_SingleTreatment_ShouldFollowAbsorptionCurve()
    {
        var treatment = new Treatment
        {
            Carbs = 8,
            Mills = new DateTimeOffset(2015, 5, 29, 4, 40, 40, 174, TimeSpan.Zero).ToUnixTimeMilliseconds(),
        };
        var treatments = new List<Treatment> { treatment };

        var rightAfterTime = new DateTimeOffset(2015, 5, 29, 4, 41, 40, 174, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var later1Time = new DateTimeOffset(2015, 5, 29, 5, 4, 40, 174, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var later2Time = new DateTimeOffset(2015, 5, 29, 5, 20, 0, 174, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var later3Time = new DateTimeOffset(2015, 5, 29, 5, 50, 0, 174, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var later4Time = new DateTimeOffset(2015, 5, 29, 6, 50, 0, 174, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var result1 = await _cobService.CobTotalAsync(treatments, rightAfterTime);
        var result2 = await _cobService.CobTotalAsync(treatments, later1Time);
        var result3 = await _cobService.CobTotalAsync(treatments, later2Time);
        var result4 = await _cobService.CobTotalAsync(treatments, later3Time);
        var result5 = await _cobService.CobTotalAsync(treatments, later4Time);

        Assert.Equal(8.0, result1.Cob, 1);
        Assert.Equal(6.0, result2.Cob, 1);
        Assert.Equal(0.0, result3.Cob, 1);
        Assert.Equal(0.0, result4.Cob, 1);
        Assert.Equal(0.0, result5.Cob, 1);
    }

    #region ApsSnapshot COB Priority Tests

    [Fact]
    public async Task CobTotal_UsesApsSnapshotCob_WhenAvailable()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Carbs = 30, Mills = time - 30 * 60 * 1000 },
        };

        var apsSnapshot = new ApsSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time - 5 * 60 * 1000).UtcDateTime,
            Cob = 15.0,
            AidAlgorithm = AidAlgorithm.Loop,
            Device = "loop://iPhone",
        };

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { apsSnapshot });

        var result = await _cobService.CobTotalAsync(treatments, time);

        Assert.Equal(15.0, result.Cob);
        Assert.Equal("Loop", result.Source);
        Assert.Equal("loop://iPhone", result.Device);
    }

    [Fact]
    public async Task CobTotal_UsesOpenApsSnapshotCob_WhenAvailable()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Carbs = 20, Mills = time - 1 },
        };

        var apsSnapshot = new ApsSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time - 1).UtcDateTime,
            Cob = 5.0,
            AidAlgorithm = AidAlgorithm.OpenAps,
            Device = "openaps://pi1",
        };

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { apsSnapshot });

        var result = await _cobService.CobTotalAsync(treatments, time);

        Assert.Equal(5.0, result.Cob);
        Assert.Equal("OpenAPS", result.Source);
        Assert.Equal("openaps://pi1", result.Device);
    }

    [Fact]
    public async Task CobTotal_FallsBackToTreatments_WhenNoApsSnapshot()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment> { new() { Carbs = 20, Mills = time - 1 } };

        var result = await _cobService.CobTotalAsync(treatments, time);

        Assert.Equal("Care Portal", result.Source);
        Assert.True(result.Cob > 0);
    }

    [Fact]
    public async Task CobTotal_FallsBackToTreatments_WhenApsSnapshotStale()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Carbs = 30, Mills = time - 30 * 60 * 1000 },
        };

        // ApsSnapshot older than 30 minutes - should be filtered out by the time range query
        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());

        var result = await _cobService.CobTotalAsync(treatments, time);

        Assert.True(result.Cob > 0);
        Assert.Equal("Care Portal", result.Source);
    }

    [Fact]
    public async Task CobTotal_FallsBackToTreatments_WhenApsSnapshotHasZeroCob()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment> { new() { Carbs = 20, Mills = time - 1 } };

        var apsSnapshot = new ApsSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time - 1).UtcDateTime,
            Cob = 0,
            AidAlgorithm = AidAlgorithm.OpenAps,
            Device = "openaps://pi1",
        };

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { apsSnapshot });

        var result = await _cobService.CobTotalAsync(treatments, time);

        Assert.Equal("Care Portal", result.Source);
        Assert.True(result.Cob > 0);
    }

    #endregion

    #region Helper Methods and Test Profile

    private static IobService CreateDefaultIobService()
    {
        var apsRepo = new Mock<IApsSnapshotRepository>();
        apsRepo.Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());
        var pumpRepo = new Mock<IPumpSnapshotRepository>();
        pumpRepo.Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<PumpSnapshot>());
        var timelineResolver = new Mock<ITherapyTimelineResolver>();
        var snapshot = new TherapySnapshot(
            dia: DefaultDIA,
            peakMinutes: TherapySnapshot.DefaultPeakMinutes,
            carbsPerHour: DefaultCarbAbsorptionRate,
            timezone: null,
            ccpPercentage: null,
            ccpTimeshiftMs: 0,
            sensitivityEntries: new[] { new ScheduleEntry { Value = DefaultSensitivity, TimeAsSeconds = 0 } },
            carbRatioEntries: new[] { new ScheduleEntry { Value = DefaultCarbRatio, TimeAsSeconds = 0 } },
            basalEntries: new[] { new ScheduleEntry { Value = DefaultBasalRate, TimeAsSeconds = 0 } }
        );
        timelineResolver
            .Setup(r => r.GetSnapshotAtAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);

        return new IobService(apsRepo.Object, pumpRepo.Object, timelineResolver.Object);
    }

    private sealed class OrefIobAdapter : IIobService
    {
        public Task<IobResult> CalculateTotalAsync(
            List<Treatment> treatments,
            long? time = null, string? specProfile = null,
            List<Nocturne.Core.Models.V4.TempBasal>? tempBasals = null,
            CancellationToken ct = default)
        {
            var currentTime = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var orefProfile = new OrefModels.OrefProfile
            {
                Dia = DefaultDIA, Sens = DefaultSensitivity,
                CarbRatio = DefaultCarbRatio, CurrentBasal = DefaultBasalRate, Curve = "bilinear",
            };
            var orefTreatments = BuildTreatments(treatments);
            var iobData = OrefService.CalculateIob(orefProfile, orefTreatments, DateTimeOffset.FromUnixTimeMilliseconds(currentTime));
            if (iobData == null)
                return Task.FromResult(new IobResult { Iob = 0.0, Activity = 0.0, Source = "Care Portal" });
            return Task.FromResult(new IobResult { Iob = iobData.Iob, Activity = iobData.Activity * DefaultSensitivity, Source = "Care Portal" });
        }

        public IobResult FromTreatments(IReadOnlyList<Treatment> treatments, long currentTime, TherapySnapshot snapshot)
            => CalculateTotalAsync(treatments.ToList(), currentTime).GetAwaiter().GetResult();
        public IobResult FromTempBasals(IReadOnlyList<Nocturne.Core.Models.V4.TempBasal> tempBasals, long currentTime, TherapySnapshot snapshot) => new();
        public IobContribution CalcTreatment(Treatment treatment, long currentTime, TherapySnapshot snapshot) => new();
        public IobContribution CalcBasalTreatment(Treatment treatment, long currentTime, TherapySnapshot snapshot) => new();
        public IobContribution CalcTempBasalIob(Nocturne.Core.Models.V4.TempBasal tempBasal, long currentTime, TherapySnapshot snapshot) => new();

        private static List<OrefModels.OrefTreatment> BuildTreatments(IEnumerable<Treatment> treatments)
        {
            return treatments.Select(t => new OrefModels.OrefTreatment
            {
                EventType = t.EventType, Mills = t.Mills, Insulin = t.Insulin,
                Carbs = t.Carbs, Rate = t.Rate,
                Duration = t.Duration.HasValue ? (int?)Math.Round(t.Duration.Value) : null,
            }).ToList();
        }
    }

    #endregion
}
