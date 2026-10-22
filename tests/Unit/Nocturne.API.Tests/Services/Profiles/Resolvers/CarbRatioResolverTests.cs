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

public class CarbRatioResolverTests : IDisposable
{
    private readonly Mock<ICarbRatioScheduleRepository> _repo = new();
    private readonly Mock<ITherapySettingsRepository> _therapyRepo = new();
    private readonly Mock<IPatientRecordRepository> _patientRecordRepo = new();
    private readonly Mock<IActiveProfileResolver> _activeProfileResolver = ActiveProfileResolverMock.WithDefault();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly CarbRatioResolver _sut;

    private const long NoonMills = 1705320000000;

    public CarbRatioResolverTests()
    {

        _sut = new CarbRatioResolver(
            _repo.Object,
            _therapyRepo.Object,
            _patientRecordRepo.Object,
            _activeProfileResolver.Object,
            new ProfileVersionCache(TestTenantCache.Create(_cache)),
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
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(new[] { schedule });

        var result = await _sut.GetCarbRatioAsync(NoonMills);

        result.Should().Be(12.0);
    }

    [Fact]
    public async Task AppliesInverseCcpPercentageScaling()
    {
        var schedule = MakeSchedule((0, 12.0));
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(new[] { schedule });
        _activeProfileResolver.Setup(r => r.GetCircadianAdjustmentAsync(NoonMills, default))
            .ReturnsAsync(new CircadianAdjustment(150, 0));

        var result = await _sut.GetCarbRatioAsync(NoonMills);

        result.Should().Be(8.0); // 12 * 100 / 150
    }

    [Fact]
    public async Task ReturnsDefaultWhenNoScheduleExists()
    {
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(Array.Empty<CarbRatioSchedule>());

        var result = await _sut.GetCarbRatioAsync(NoonMills);

        result.Should().Be(12.0);
    }

    [Fact]
    public async Task UsesActiveProfileNameWhenSpecProfileIsNull()
    {
        _activeProfileResolver.Setup(r => r.GetActiveProfileNameAsync(NoonMills, default))
            .ReturnsAsync("Weekday");
        var schedule = MakeSchedule((0, 8.0));
        _repo.Setup(r => r.GetByProfileNameAsync("Weekday", default))
            .ReturnsAsync(new[] { schedule });

        var result = await _sut.GetCarbRatioAsync(NoonMills);

        result.Should().Be(8.0);
    }

    [Fact]
    public async Task UsesSpecProfileWhenProvided()
    {
        var schedule = MakeSchedule((0, 20.0));
        _repo.Setup(r => r.GetByProfileNameAsync("Custom", default))
            .ReturnsAsync(new[] { schedule });

        var result = await _sut.GetCarbRatioAsync(NoonMills, specProfile: "Custom");

        result.Should().Be(20.0);
        _activeProfileResolver.Verify(r => r.GetActiveProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LookupsAcrossAScheduleEditEachGetTheirOwnVersion(bool earlyLookupFirst)
    {
        var olderVersion = MakeSchedule((0, 10.0));
        olderVersion.Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(NoonMills).UtcDateTime.AddDays(-1);
        var editedVersion = MakeSchedule((0, 14.0));
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
            early = await _sut.GetCarbRatioAsync(beforeEdit);
            late = await _sut.GetCarbRatioAsync(NoonMills);
        }
        else
        {
            late = await _sut.GetCarbRatioAsync(NoonMills);
            early = await _sut.GetCarbRatioAsync(beforeEdit);
        }

        early.Should().Be(10.0);
        late.Should().Be(14.0);
    }
}
