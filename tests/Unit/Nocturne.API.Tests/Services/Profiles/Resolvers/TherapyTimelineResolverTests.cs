using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Services.Profiles.Resolvers;

public class TherapyTimelineResolverTests
{
    private const long From = 1705305600000;
    private const long To = 1705348800000;

    private readonly Mock<IStateSpanService> _stateSpans = new();
    private readonly Mock<ITherapySettingsResolver> _therapySettings = new();
    private readonly Mock<IActiveProfileResolver> _activeProfile = new();
    private readonly TherapyTimelineResolver _sut;

    public TherapyTimelineResolverTests()
    {
        _therapySettings.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _sut = new TherapyTimelineResolver(
            _stateSpans.Object,
            _therapySettings.Object,
            _activeProfile.Object,
            Mock.Of<ISensitivityScheduleRepository>(),
            Mock.Of<ICarbRatioScheduleRepository>(),
            Mock.Of<IBasalScheduleRepository>(),
            NullLogger<TherapyTimelineResolver>.Instance);
    }

    private void SetupSpans(params StateSpan[] spans) =>
        _stateSpans
            .Setup(s => s.GetStateSpansAsync(
                StateSpanCategory.Profile,
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<bool?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(spans);

    [Fact]
    public async Task BuildAsync_BoundsSpanQueryByWindow_AndLongRunningSpanAddsNoBoundary()
    {
        SetupSpans(new StateSpan
        {
            Id = Guid.NewGuid().ToString(),
            Category = StateSpanCategory.Profile,
            State = "Active",
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(From).UtcDateTime.AddDays(-90),
            EndTimestamp = null,
        });

        var timeline = await _sut.BuildAsync(From, To);

        timeline.Segments.Should().ContainSingle();
        _stateSpans.Verify(
            s => s.GetStateSpansAsync(
                StateSpanCategory.Profile,
                It.IsAny<string?>(),
                DateTimeOffset.FromUnixTimeMilliseconds(From).UtcDateTime,
                DateTimeOffset.FromUnixTimeMilliseconds(To).UtcDateTime,
                It.IsAny<string?>(),
                It.IsAny<bool?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
