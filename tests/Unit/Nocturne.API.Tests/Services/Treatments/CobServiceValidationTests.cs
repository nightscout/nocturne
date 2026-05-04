using Microsoft.Extensions.Logging;
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
/// Simple validation tests for the COB service with V4 resolvers
/// </summary>
public class CobServiceValidationTests
{
    private readonly ICobService _cobService;
    private readonly Mock<IApsSnapshotRepository> _apsSnapshotRepo;

    public CobServiceValidationTests()
    {
        var logger = new Mock<ILogger<Nocturne.API.Services.Treatments.CobService>>();
        var iobService = new Mock<IIobService>();
        iobService
            .Setup(s => s.FromTreatments(It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<long>(), It.IsAny<TherapySnapshot>()))
            .Returns(new IobResult { Activity = 0.0 });

        var sensitivity = new Mock<ISensitivityResolver>();
        sensitivity.Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(50.0);
        var carbRatio = new Mock<ICarbRatioResolver>();
        carbRatio.Setup(c => c.GetCarbRatioAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(15.0);
        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        therapySettings.Setup(t => t.GetCarbAbsorptionRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(30.0);

        _apsSnapshotRepo = new Mock<IApsSnapshotRepository>();
        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());

        var timelineResolver = new Mock<ITherapyTimelineResolver>();
        var snapshot = new TherapySnapshot(
            dia: 3.0,
            peakMinutes: TherapySnapshot.DefaultPeakMinutes,
            carbsPerHour: 30.0,
            timezone: null,
            ccpPercentage: null,
            ccpTimeshiftMs: 0,
            sensitivityEntries: new[] { new ScheduleEntry { Value = 50.0, TimeAsSeconds = 0 } },
            carbRatioEntries: new[] { new ScheduleEntry { Value = 15.0, TimeAsSeconds = 0 } },
            basalEntries: null
        );
        timelineResolver
            .Setup(r => r.GetSnapshotAtAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);

        _cobService = new Nocturne.API.Services.Treatments.CobService(
            logger.Object, iobService.Object, _apsSnapshotRepo.Object, timelineResolver.Object);
    }

    [Fact]
    public async Task CobTotal_WithCarbs_ShouldReturnPositiveCob()
    {
        var treatments = new List<Treatment>
        {
            new() { Carbs = 50, Mills = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds() },
        };

        var result = await _cobService.CobTotalAsync(treatments);

        Assert.True(result.Cob >= 0, "COB should be non-negative");
        Assert.Equal("Care Portal", result.Source);
    }

    [Fact]
    public async Task CobTotal_WithoutCarbs_ShouldReturnZeroCob()
    {
        var treatments = new List<Treatment>
        {
            new() { Insulin = 5.0, Mills = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds() },
        };

        var result = await _cobService.CobTotalAsync(treatments);

        Assert.Equal(0.0, result.Cob);
    }

    [Fact]
    public async Task CobTotal_WithRecentApsSnapshot_ShouldPrioritizeDeviceData()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Carbs = 50, Mills = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds() },
        };

        var apsSnapshot = new ApsSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time - 5 * 60 * 1000).UtcDateTime,
            Cob = 25.5,
            AidAlgorithm = AidAlgorithm.Loop,
            Device = "Loop",
        };

        _apsSnapshotRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { apsSnapshot });

        var result = await _cobService.CobTotalAsync(treatments);

        Assert.Equal(25.5, result.Cob);
        Assert.Equal("Loop", result.Source);
    }
}
