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

public class BasalRateResolverTests : IDisposable
{
    private readonly Mock<IBasalScheduleRepository> _repo = new();
    private readonly Mock<ITherapySettingsRepository> _therapyRepo = new();
    private readonly Mock<IActiveProfileResolver> _activeProfileResolver = new();
    private readonly Mock<ITenantAccessor> _tenantAccessor = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly BasalRateResolver _sut;

    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    // 2024-01-15 12:00:00 UTC
    private const long NoonMills = 1705320000000;

    public BasalRateResolverTests()
    {
        _tenantAccessor.Setup(t => t.TenantId).Returns(TenantId);

        _sut = new BasalRateResolver(
            _repo.Object,
            _therapyRepo.Object,
            _activeProfileResolver.Object,
            _tenantAccessor.Object,
            _cache,
            NullLogger<BasalRateResolver>.Instance);
    }

    public void Dispose() => _cache.Dispose();

    private static BasalSchedule MakeSchedule(params (int seconds, double value)[] entries) => new()
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
        var schedule = MakeSchedule((0, 0.8), (6 * 3600, 1.0), (22 * 3600, 0.9));
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync(schedule);

        var result = await _sut.GetBasalRateAsync(NoonMills);

        result.Should().Be(1.0);
    }

    [Fact]
    public async Task AppliesCcpPercentageScaling()
    {
        var schedule = MakeSchedule((0, 1.0));
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync(schedule);
        _activeProfileResolver.Setup(r => r.GetCircadianAdjustmentAsync(NoonMills, default))
            .ReturnsAsync(new CircadianAdjustment(150, 0));

        var result = await _sut.GetBasalRateAsync(NoonMills);

        result.Should().Be(1.5);
    }

    [Fact]
    public async Task ReturnsDefaultWhenNoScheduleExists()
    {
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync((BasalSchedule?)null);

        var result = await _sut.GetBasalRateAsync(NoonMills);

        result.Should().Be(1.0);
    }

    [Fact]
    public async Task UsesActiveProfileNameWhenSpecProfileIsNull()
    {
        _activeProfileResolver.Setup(r => r.GetActiveProfileNameAsync(NoonMills, default))
            .ReturnsAsync("Weekday");
        var schedule = MakeSchedule((0, 2.0));
        _repo.Setup(r => r.GetActiveAtAsync("Weekday", It.IsAny<DateTime>(), default))
            .ReturnsAsync(schedule);

        var result = await _sut.GetBasalRateAsync(NoonMills);

        result.Should().Be(2.0);
    }

    [Fact]
    public async Task UsesSpecProfileWhenProvided()
    {
        var schedule = MakeSchedule((0, 3.0));
        _repo.Setup(r => r.GetActiveAtAsync("Custom", It.IsAny<DateTime>(), default))
            .ReturnsAsync(schedule);

        var result = await _sut.GetBasalRateAsync(NoonMills, specProfile: "Custom");

        result.Should().Be(3.0);
        _activeProfileResolver.Verify(r => r.GetActiveProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
