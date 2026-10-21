using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Profiles.Resolvers;

public sealed class TherapyTimelineResolverTests : IDisposable
{
    private const long From = 1705305600000;
    private const long To = 1705348800000;
    private const long Middle = (From + To) / 2;

    private readonly Mock<ITherapySettingsResolver> _therapySettings = new();
    private readonly Mock<IActiveProfileResolver> _activeProfile = ActiveProfileResolverMock.WithDefault();
    private readonly Mock<ISensitivityScheduleRepository> _sensitivity = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly TherapyTimelineResolver _sut;

    public TherapyTimelineResolverTests()
    {
        _therapySettings.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _sut = new TherapyTimelineResolver(
            _therapySettings.Object,
            _activeProfile.Object,
            Mock.Of<ITherapySettingsRepository>(),
            _sensitivity.Object,
            Mock.Of<ICarbRatioScheduleRepository>(),
            Mock.Of<IBasalScheduleRepository>(),
            new ProfileVersionCache(TestTenantCache.Create(_cache)),
            NullLogger<TherapyTimelineResolver>.Instance);
    }

    public void Dispose() => _cache.Dispose();

    private void SetupSpans(params ProfileSpan[] spans) =>
        _activeProfile
            .Setup(a => a.GetProfileSpansForRangeAsync(From, To, It.IsAny<CancellationToken>()))
            .ReturnsAsync(spans);

    [Fact]
    public async Task BuildAsync_ReadsTheWindow_AndAStretchCoveringItAddsNoBoundary()
    {
        SetupSpans(new ProfileSpan("Rhys", From, To, null));

        var timeline = await _sut.BuildAsync(From, To);

        timeline.Segments.Should().ContainSingle();
        _activeProfile.Verify(
            a => a.GetProfileSpansForRangeAsync(From, To, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BuildAsync_SplitsAtEachStretchEdgeInsideTheWindow()
    {
        SetupSpans(new ProfileSpan("Default", From, Middle, null), new ProfileSpan("Rhys", Middle, To, null));

        var timeline = await _sut.BuildAsync(From, To);

        timeline.Segments.Select(s => (s.StartMills, s.EndMills)).Should().Equal((From, Middle), (Middle, To));
    }

    [Fact]
    public async Task BuildAsync_SplitsWhereANewVersionOfTheProfileTakesEffect_AndReadsEachVersionFromItsStart()
    {
        _therapySettings.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        SetupSpans(new ProfileSpan("Rhys", From, To, null));
        _sensitivity.Setup(r => r.GetByProfileNameAsync("Rhys", It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            Sensitivity(Middle, 65),
            Sensitivity(From - 86_400_000, 35),
        ]);

        var timeline = await _sut.BuildAsync(From, To);

        timeline.Segments.Select(s => (s.StartMills, s.EndMills)).Should().Equal((From, Middle), (Middle, To));
        timeline.SnapshotAt(From).SensitivityAt(From).Should().Be(35);
        timeline.SnapshotAt(Middle).SensitivityAt(Middle).Should().Be(65);
    }

    private static SensitivitySchedule Sensitivity(long fromMills, double value) => new()
    {
        ProfileName = "Rhys",
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(fromMills).UtcDateTime,
        Entries = [new ScheduleEntry { Time = "00:00", TimeAsSeconds = 0, Value = value }],
    };

    /// <summary>
    /// A customized snapshot already has its percentage applied, so the segment reads its schedules
    /// with no CCP: its span carries no <see cref="ProfileSpan.Adjustment"/>.
    /// </summary>
    [Fact]
    public async Task BuildAsync_ReadsAnActivationsOwnSchedulesWithoutScalingThemAgain()
    {
        SetupSpans(new ProfileSpan("Rhys", From, To, null, new ProfileActivationSnapshot
        {
            Basal = [new ScheduleEntry { Time = "00:00", TimeAsSeconds = 0, Value = 1.1 }],
        }));

        var timeline = await _sut.BuildAsync(From, To);

        var snapshot = timeline.SnapshotAt(From);
        snapshot.CcpPercentage.Should().BeNull();
        snapshot.BasalRateAt(From).Should().Be(1.1);
    }

    [Fact]
    public async Task BuildAsync_ReadsAnActivationsOwnDia()
    {
        _therapySettings.Setup(t => t.GetDIAAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(4);
        SetupSpans(new ProfileSpan("Rhys", From, To, null, new ProfileActivationSnapshot
        {
            Basal = [new ScheduleEntry { Time = "00:00", TimeAsSeconds = 0, Value = 1.1 }],
            Insulin = new TreatmentInsulinContext { InsulinName = "Fiasp", Dia = 7, Peak = 55 },
        }));

        var timeline = await _sut.BuildAsync(From, To);

        timeline.SnapshotAt(From).Dia.Should().Be(7);
    }

    [Fact]
    public async Task BuildAsync_ReadsAnActivationsOwnDia_WhenItsInsulinCarriesNone()
    {
        _therapySettings.Setup(t => t.GetDIAAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(4);
        SetupSpans(new ProfileSpan("Rhys", From, To, null, new ProfileActivationSnapshot
        {
            Basal = [new ScheduleEntry { Time = "00:00", TimeAsSeconds = 0, Value = 1.1 }],
            Dia = 6,
            Insulin = new TreatmentInsulinContext { InsulinName = "Fiasp", Dia = 0, Peak = 55 },
        }));

        var timeline = await _sut.BuildAsync(From, To);

        timeline.SnapshotAt(From).Dia.Should().Be(6);
    }
}
