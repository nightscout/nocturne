using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ChartData;
using Nocturne.API.Services.ChartData.Stages;
using Nocturne.Core.Contracts.Profiles.Resolvers;

namespace Nocturne.API.Tests.Services.ChartData.Stages;

public class ProfileLoadStageTests
{
    private readonly Mock<ITherapySettingsResolver> _therapySettingsResolver = new();
    private readonly Mock<ITargetRangeResolver> _targetRangeResolver = new();
    private readonly Mock<IBasalRateResolver> _basalRateResolver = new();

    private ProfileLoadStage CreateStage() =>
        new(
            _therapySettingsResolver.Object,
            _targetRangeResolver.Object,
            _basalRateResolver.Object,
            NullLogger<ProfileLoadStage>.Instance
        );

    private static ChartDataContext CreateContext(long endTime = 1700086400000L) =>
        new()
        {
            StartTime = 1700000000000L,
            EndTime = endTime,
            IntervalMinutes = 5,
            BufferStartTime = 1700000000000L - 8L * 60 * 60 * 1000,
        };

    [Fact]
    public async Task ExecuteAsync_WithProfiles_SetsContextFromResolvers()
    {
        // Arrange
        var endTime = 1700086400000L;

        _therapySettingsResolver
            .Setup(s => s.HasDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _therapySettingsResolver
            .Setup(s => s.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("America/New_York");
        _targetRangeResolver
            .Setup(s => s.GetLowBGTargetAsync(endTime, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(80.0);
        _targetRangeResolver
            .Setup(s => s.GetHighBGTargetAsync(endTime, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(160.0);
        _basalRateResolver
            .Setup(s => s.GetBasalRateAsync(endTime, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.8);

        var stage = CreateStage();
        var context = CreateContext(endTime);

        // Act
        var result = await stage.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Timezone.Should().Be("America/New_York");
        result.Thresholds.VeryLow.Should().Be(54);
        result.Thresholds.Low.Should().Be(80.0);
        result.Thresholds.High.Should().Be(160.0);
        result.Thresholds.VeryHigh.Should().Be(250);
        result.DefaultBasalRate.Should().Be(0.8);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoProfiles_UsesDefaults()
    {
        // Arrange
        _therapySettingsResolver
            .Setup(s => s.HasDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var stage = CreateStage();
        var context = CreateContext();

        // Act
        var result = await stage.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Timezone.Should().BeNull();
        result.Thresholds.VeryLow.Should().Be(54);
        result.Thresholds.Low.Should().Be(70);
        result.Thresholds.High.Should().Be(180);
        result.Thresholds.VeryHigh.Should().Be(250);
        result.DefaultBasalRate.Should().Be(1.0);
    }
}
