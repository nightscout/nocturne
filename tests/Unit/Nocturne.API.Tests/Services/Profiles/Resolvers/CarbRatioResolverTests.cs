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

public class CarbRatioResolverTests : IDisposable
{
    private readonly Mock<ICarbRatioScheduleRepository> _repo = new();
    private readonly Mock<ITherapySettingsRepository> _therapyRepo = new();
    private readonly Mock<IActiveProfileResolver> _activeProfileResolver = new();
    private readonly Mock<ITenantAccessor> _tenantAccessor = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly CarbRatioResolver _sut;

    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const long NoonMills = 1705320000000;

    public CarbRatioResolverTests()
    {
        _tenantAccessor.Setup(t => t.TenantId).Returns(TenantId);

        _sut = new CarbRatioResolver(
            _repo.Object,
            _therapyRepo.Object,
            _activeProfileResolver.Object,
            _tenantAccessor.Object,
            _cache,
            NullLogger<CarbRatioResolver>.Instance);
    }

    public void Dispose() => _cache.Dispose();

    private static CarbRatioSchedule MakeSchedule(params (int seconds, double value)[] entries) => new()
    {
        Id = Guid.NewGuid(),
        ProfileName = "Default",
        Entries = entries.Select(e => new ScheduleEntry
        {
            TimeAsSeconds = e.seconds,
            Value = e.value,
        }).ToList(),
    };

    [Fact]
    public async Task ReturnsCorrectValueFromSchedule()
    {
        var schedule = MakeSchedule((0, 10.0), (6 * 3600, 12.0), (22 * 3600, 15.0));
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync(schedule);

        var result = await _sut.GetCarbRatioAsync(NoonMills);

        result.Should().Be(12.0);
    }

    [Fact]
    public async Task AppliesInverseCcpPercentageScaling()
    {
        var schedule = MakeSchedule((0, 12.0));
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync(schedule);
        _activeProfileResolver.Setup(r => r.GetCircadianAdjustmentAsync(NoonMills, default))
            .ReturnsAsync(new CircadianAdjustment(150, 0));

        var result = await _sut.GetCarbRatioAsync(NoonMills);

        result.Should().Be(8.0); // 12 * 100 / 150
    }

    [Fact]
    public async Task ReturnsDefaultWhenNoScheduleExists()
    {
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync((CarbRatioSchedule?)null);

        var result = await _sut.GetCarbRatioAsync(NoonMills);

        result.Should().Be(12.0);
    }

    [Fact]
    public async Task UsesActiveProfileNameWhenSpecProfileIsNull()
    {
        _activeProfileResolver.Setup(r => r.GetActiveProfileNameAsync(NoonMills, default))
            .ReturnsAsync("Weekday");
        var schedule = MakeSchedule((0, 8.0));
        _repo.Setup(r => r.GetActiveAtAsync("Weekday", It.IsAny<DateTime>(), default))
            .ReturnsAsync(schedule);

        var result = await _sut.GetCarbRatioAsync(NoonMills);

        result.Should().Be(8.0);
    }

    [Fact]
    public async Task UsesSpecProfileWhenProvided()
    {
        var schedule = MakeSchedule((0, 20.0));
        _repo.Setup(r => r.GetActiveAtAsync("Custom", It.IsAny<DateTime>(), default))
            .ReturnsAsync(schedule);

        var result = await _sut.GetCarbRatioAsync(NoonMills, specProfile: "Custom");

        result.Should().Be(20.0);
        _activeProfileResolver.Verify(r => r.GetActiveProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
