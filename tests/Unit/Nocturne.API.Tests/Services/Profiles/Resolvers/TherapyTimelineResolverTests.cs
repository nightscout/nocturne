using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Profiles.Resolvers;

public class TherapyTimelineResolverTests
{
    private const long From = 1705305600000;
    private const long To = 1705348800000;
    private const long Middle = (From + To) / 2;

    private readonly Mock<ITherapySettingsResolver> _therapySettings = new();
    private readonly Mock<IActiveProfileResolver> _activeProfile = ActiveProfileResolverMock.WithDefault();
    private readonly TherapyTimelineResolver _sut;

    public TherapyTimelineResolverTests()
    {
        _therapySettings.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _sut = new TherapyTimelineResolver(
            _therapySettings.Object,
            _activeProfile.Object,
            Mock.Of<ISensitivityScheduleRepository>(),
            Mock.Of<ICarbRatioScheduleRepository>(),
            Mock.Of<IBasalScheduleRepository>(),
            NullLogger<TherapyTimelineResolver>.Instance);
    }

    private void SetupSpans(params ProfileSpan[] spans) =>
        _activeProfile
            .Setup(a => a.GetActiveProfileSpansForRangeAsync(From, To, It.IsAny<CancellationToken>()))
            .ReturnsAsync(spans);

    [Fact]
    public async Task BuildAsync_ReadsTheWindow_AndAStretchCoveringItAddsNoBoundary()
    {
        SetupSpans(new ProfileSpan("Rhys", From, To, null));

        var timeline = await _sut.BuildAsync(From, To);

        timeline.Segments.Should().ContainSingle();
        _activeProfile.Verify(
            a => a.GetActiveProfileSpansForRangeAsync(From, To, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BuildAsync_SplitsAtEachStretchEdgeInsideTheWindow()
    {
        SetupSpans(new ProfileSpan("Rhys", Middle, To, null));

        var timeline = await _sut.BuildAsync(From, To);

        timeline.Segments.Select(s => (s.StartMills, s.EndMills)).Should().Equal((From, Middle), (Middle, To));
    }

    /// <summary>
    /// A customized snapshot already has its percentage applied, so the segment reads its schedules
    /// with no CCP: <see cref="IActiveProfileResolver.GetCircadianAdjustmentAsync"/> answers null for it.
    /// </summary>
    [Fact]
    public async Task BuildAsync_ReadsAnActivationsOwnSchedulesWithoutScalingThemAgain()
    {
        SetupSpans(new ProfileSpan("Rhys", From, To, null));
        _activeProfile.Setup(a => a.GetActiveProfileNameAsync(From, It.IsAny<CancellationToken>())).ReturnsAsync("Rhys");
        _activeProfile.Setup(a => a.GetActiveSchedulesAsync(From, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileActivationSnapshot
            {
                Basal = [new ScheduleEntry { Time = "00:00", TimeAsSeconds = 0, Value = 1.1 }],
            });

        var timeline = await _sut.BuildAsync(From, To);

        var snapshot = timeline.SnapshotAt(From);
        snapshot.CcpPercentage.Should().BeNull();
        snapshot.BasalRateAt(From).Should().Be(1.1);
    }
}
