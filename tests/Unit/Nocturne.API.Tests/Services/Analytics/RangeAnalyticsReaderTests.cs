using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Analytics;

[Trait("Category", "Unit")]
public class RangeAnalyticsReaderTests
{
    private static readonly DateTime Start = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = Start.AddDays(7);

    private readonly Mock<ISensorGlucoseRepository> _glucose = new();
    private readonly Mock<IBolusRepository> _boluses = new();
    private readonly Mock<ICarbIntakeRepository> _carbs = new();
    private readonly Mock<ITempBasalRepository> _tempBasals = new();
    private readonly Mock<IBasalInjectionRepository> _injections = new();
    private readonly RangeAnalyticsReader _reader;
    private readonly InsulinDeliveryReader _insulinDelivery;

    public RangeAnalyticsReaderTests()
    {
        var therapy = new Mock<ITherapySettingsResolver>();
        therapy
            .Setup(r => r.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("UTC");
        var statistics = new StatisticsService(TimeProvider.System);

        _insulinDelivery = new InsulinDeliveryReader(
            _boluses.Object,
            _carbs.Object,
            _tempBasals.Object,
            _injections.Object,
            Mock.Of<IStateSpanRepository>(),
            Mock.Of<IBasalSegmentService>(),
            therapy.Object,
            statistics,
            TimeProvider.System);
        _reader = new RangeAnalyticsReader(
            _glucose.Object,
            _boluses.Object,
            _carbs.Object,
            Mock.Of<IPatientDeviceRepository>(),
            TestDoubles.CanonicalGlucosePassThrough.Create(),
            statistics,
            new TenantClockResolver(therapy.Object, new CategoryReadContext(), Mock.Of<ILogger<TenantClockResolver>>()),
            Mock.Of<IActiveProfileResolver>(),
            Mock.Of<ITargetRangeScheduleRepository>(),
            _insulinDelivery,
            TimeProvider.System);
    }

    [Fact]
    public async Task ReadWithInsulinDeliveryAsync_ReadsTheWindowsCarbsAndManualBolusesOnce()
    {
        await _reader.ReadWithInsulinDeliveryAsync(Start, End, DiabetesPopulation.Type1Adult, CancellationToken.None);

        VerifyCarbReads(Times.Once());
        VerifyBolusReads(BolusKind.Manual, Times.Once());
        VerifyBolusReads(BolusKind.Algorithm, Times.Once());
    }

    [Fact]
    public async Task ReadAsync_ReadsTheWindowsCarbsAndManualBolusesOnce()
    {
        await _reader.ReadAsync(Start, End, DiabetesPopulation.Type1Adult, null, CancellationToken.None);

        VerifyCarbReads(Times.Once());
        VerifyBolusReads(BolusKind.Manual, Times.Once());
        VerifyBolusReads(BolusKind.Algorithm, Times.Never());
    }

    [Fact]
    public async Task InsulinDeliveryReadAsync_ReadsTheWindowsCarbsAndEachBolusKindOnce()
    {
        await _insulinDelivery.ReadAsync(Start, End, CancellationToken.None);

        VerifyCarbReads(Times.Once());
        VerifyBolusReads(BolusKind.Manual, Times.Once());
        VerifyBolusReads(BolusKind.Algorithm, Times.Once());
    }

    private void VerifyCarbReads(Times times) =>
        _carbs.Verify(r => r.GetAsync(
            Start, End, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), times);

    private void VerifyBolusReads(BolusKind kind, Times times) =>
        _boluses.Verify(r => r.GetAsync(
            Start, End, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
            kind, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), times);
}
