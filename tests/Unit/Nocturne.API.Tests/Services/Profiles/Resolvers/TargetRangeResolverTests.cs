using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Profiles.Resolvers;

public class TargetRangeResolverTests : IDisposable
{
    private readonly Mock<ITargetRangeScheduleRepository> _repo = new();
    private readonly Mock<ITherapySettingsRepository> _therapyRepo = new();
    private readonly Mock<IActiveProfileResolver> _activeProfileResolver = new();
    private readonly Mock<ITenantAccessor> _tenantAccessor = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly TargetRangeResolver _sut;

    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const long NoonMills = 1705320000000;

    public TargetRangeResolverTests()
    {
        _tenantAccessor.Setup(t => t.TenantId).Returns(TenantId);

        _sut = new TargetRangeResolver(
            _repo.Object,
            _therapyRepo.Object,
            _activeProfileResolver.Object,
            _tenantAccessor.Object,
            _cache,
            NullLogger<TargetRangeResolver>.Instance);
    }

    public void Dispose() => _cache.Dispose();

    private static TargetRangeSchedule MakeSchedule(params (int seconds, double low, double high)[] entries) => new()
    {
        Id = Guid.NewGuid(),
        ProfileName = "Default",
        Entries = entries.Select(e => new TargetRangeEntry
        {
            TimeAsSeconds = e.seconds,
            Low = e.low,
            High = e.high,
        }).ToList(),
    };

    [Fact]
    public async Task ReturnsCorrectLowTarget()
    {
        var schedule = MakeSchedule((0, 80.0, 120.0), (6 * 3600, 90.0, 130.0));
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync(schedule);

        var result = await _sut.GetLowBGTargetAsync(NoonMills);

        result.Should().Be(90.0);
    }

    [Fact]
    public async Task ReturnsCorrectHighTarget()
    {
        var schedule = MakeSchedule((0, 80.0, 120.0), (6 * 3600, 90.0, 130.0));
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync(schedule);

        var result = await _sut.GetHighBGTargetAsync(NoonMills);

        result.Should().Be(130.0);
    }

    [Fact]
    public async Task DoesNotApplyCcpAdjustmentToTargets()
    {
        var schedule = MakeSchedule((0, 80.0, 120.0));
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync(schedule);
        _activeProfileResolver.Setup(r => r.GetCircadianAdjustmentAsync(NoonMills, default))
            .ReturnsAsync(new CircadianAdjustment(200, 0));

        var low = await _sut.GetLowBGTargetAsync(NoonMills);
        var high = await _sut.GetHighBGTargetAsync(NoonMills);

        low.Should().Be(80.0);
        high.Should().Be(120.0);
    }

    [Fact]
    public async Task ReturnsDefaultsWhenNoScheduleExists()
    {
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync((TargetRangeSchedule?)null);

        var low = await _sut.GetLowBGTargetAsync(NoonMills);
        var high = await _sut.GetHighBGTargetAsync(NoonMills);

        low.Should().Be(70.0);
        high.Should().Be(180.0);
    }

    [Fact]
    public async Task UsesActiveProfileNameWhenSpecProfileIsNull()
    {
        _activeProfileResolver.Setup(r => r.GetActiveProfileNameAsync(NoonMills, default))
            .ReturnsAsync("Night");
        var schedule = MakeSchedule((0, 100.0, 140.0));
        _repo.Setup(r => r.GetActiveAtAsync("Night", It.IsAny<DateTime>(), default))
            .ReturnsAsync(schedule);

        var result = await _sut.GetLowBGTargetAsync(NoonMills);

        result.Should().Be(100.0);
    }
}
