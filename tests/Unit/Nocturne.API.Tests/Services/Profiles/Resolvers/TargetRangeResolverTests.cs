using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.API.Tests.Infrastructure;

namespace Nocturne.API.Tests.Services.Profiles.Resolvers;

public class TargetRangeResolverTests : IDisposable
{
    private readonly Mock<ITargetRangeScheduleRepository> _repo = new();
    private readonly Mock<ITherapySettingsRepository> _therapyRepo = new();
    private readonly Mock<IPatientRecordRepository> _patientRecordRepo = new();
    private readonly Mock<IActiveProfileResolver> _activeProfileResolver = ActiveProfileResolverMock.WithDefault();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly TargetRangeResolver _sut;

    private const long NoonMills = 1705320000000;

    public TargetRangeResolverTests()
    {

        _sut = new TargetRangeResolver(
            _repo.Object,
            _therapyRepo.Object,
            _patientRecordRepo.Object,
            _activeProfileResolver.Object,
            new ProfileVersionCache(TestTenantCache.Create(_cache)),
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
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(new[] { schedule });

        var result = await _sut.GetLowBGTargetAsync(NoonMills);

        result.Should().Be(90.0);
    }

    [Fact]
    public async Task ReturnsCorrectHighTarget()
    {
        var schedule = MakeSchedule((0, 80.0, 120.0), (6 * 3600, 90.0, 130.0));
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(new[] { schedule });

        var result = await _sut.GetHighBGTargetAsync(NoonMills);

        result.Should().Be(130.0);
    }

    [Fact]
    public async Task RangeReturnsBothTargetsFromOneResolution()
    {
        _activeProfileResolver.Setup(r => r.GetActiveProfileNameAsync(NoonMills, default))
            .ReturnsAsync("Night");
        var schedule = MakeSchedule((0, 80.0, 120.0), (6 * 3600, 90.0, 130.0));
        _repo.Setup(r => r.GetByProfileNameAsync("Night", default))
            .ReturnsAsync(new[] { schedule });

        var (low, high) = await _sut.GetBGTargetRangeAsync(NoonMills);

        low.Should().Be(90.0);
        high.Should().Be(130.0);
        _activeProfileResolver.Verify(r => r.GetActiveProfileNameAsync(NoonMills, default), Times.Once);
        _activeProfileResolver.Verify(r => r.GetCircadianAdjustmentAsync(NoonMills, default), Times.Once);
    }

    [Fact]
    public async Task DoesNotApplyCcpAdjustmentToTargets()
    {
        var schedule = MakeSchedule((0, 80.0, 120.0));
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(new[] { schedule });
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
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(Array.Empty<TargetRangeSchedule>());

        var low = await _sut.GetLowBGTargetAsync(NoonMills);
        var high = await _sut.GetHighBGTargetAsync(NoonMills);

        low.Should().Be(70.0);
        high.Should().Be(180.0);
    }

    [Fact]
    public async Task FindReturnsNullWhenNoScheduleExists()
    {
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(Array.Empty<TargetRangeSchedule>());

        (await _sut.FindBGTargetRangeAsync(NoonMills)).Should().BeNull();
    }

    [Fact]
    public async Task FindReturnsNullWhenTheScheduleHasNoEntries()
    {
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(new[] { MakeSchedule() });

        (await _sut.FindBGTargetRangeAsync(NoonMills)).Should().BeNull();
        (await _sut.GetBGTargetRangeAsync(NoonMills)).Should().Be((70.0, 180.0));
    }

    [Fact]
    public async Task FindReturnsTheScheduledRange()
    {
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(new[] { MakeSchedule((0, 80.0, 120.0), (6 * 3600, 90.0, 130.0)) });

        (await _sut.FindBGTargetRangeAsync(NoonMills)).Should().Be((90.0, 130.0));
    }

    [Fact]
    public async Task UsesActiveProfileNameWhenSpecProfileIsNull()
    {
        _activeProfileResolver.Setup(r => r.GetActiveProfileNameAsync(NoonMills, default))
            .ReturnsAsync("Night");
        var schedule = MakeSchedule((0, 100.0, 140.0));
        _repo.Setup(r => r.GetByProfileNameAsync("Night", default))
            .ReturnsAsync(new[] { schedule });

        var result = await _sut.GetLowBGTargetAsync(NoonMills);

        result.Should().Be(100.0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LookupsAcrossAScheduleEditEachGetTheirOwnVersion(bool earlyLookupFirst)
    {
        var olderVersion = MakeSchedule((0, 80.0, 140.0));
        olderVersion.Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(NoonMills).UtcDateTime.AddDays(-1);
        var editedVersion = MakeSchedule((0, 100.0, 140.0));
        editedVersion.Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(NoonMills).UtcDateTime.AddHours(-1);
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(new[] { editedVersion, olderVersion });
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync((string _, DateTime at, CancellationToken _) =>
                at < editedVersion.Timestamp ? olderVersion : editedVersion);
        var beforeEdit = NoonMills - 2 * 3_600_000;

        double early;
        double late;
        if (earlyLookupFirst)
        {
            early = await _sut.GetLowBGTargetAsync(beforeEdit);
            late = await _sut.GetLowBGTargetAsync(NoonMills);
        }
        else
        {
            late = await _sut.GetLowBGTargetAsync(NoonMills);
            early = await _sut.GetLowBGTargetAsync(beforeEdit);
        }

        early.Should().Be(80.0);
        late.Should().Be(100.0);
    }
}
