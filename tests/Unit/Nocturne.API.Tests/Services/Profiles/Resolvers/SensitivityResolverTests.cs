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

public class SensitivityResolverTests : IDisposable
{
    private readonly Mock<ISensitivityScheduleRepository> _repo = new();
    private readonly Mock<ITherapySettingsRepository> _therapyRepo = new();
    private readonly Mock<IPatientRecordRepository> _patientRecordRepo = new();
    private readonly Mock<IActiveProfileResolver> _activeProfileResolver = ActiveProfileResolverMock.WithDefault();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly SensitivityResolver _sut;

    private const long NoonMills = 1705320000000;

    public SensitivityResolverTests()
    {

        _sut = new SensitivityResolver(
            _repo.Object,
            _therapyRepo.Object,
            _patientRecordRepo.Object,
            _activeProfileResolver.Object,
            new ProfileVersionCache(TestTenantCache.Create(_cache)),
            Mock.Of<IProfileActivationResolver>(),
            NullLogger<SensitivityResolver>.Instance);
    }

    public void Dispose() => _cache.Dispose();

    private static SensitivitySchedule MakeSchedule(params (int seconds, double value)[] entries) => new()
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
        var schedule = MakeSchedule((0, 40.0), (6 * 3600, 50.0), (22 * 3600, 60.0));
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(new[] { schedule });

        var result = await _sut.GetSensitivityAsync(NoonMills);

        result.Should().Be(50.0);
    }

    [Fact]
    public async Task AppliesInverseCcpPercentageScaling()
    {
        var schedule = MakeSchedule((0, 50.0));
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(new[] { schedule });
        _activeProfileResolver.Setup(r => r.GetCircadianAdjustmentAsync(NoonMills, default))
            .ReturnsAsync(new CircadianAdjustment(200, 0));

        var result = await _sut.GetSensitivityAsync(NoonMills);

        result.Should().Be(25.0); // 50 * 100 / 200
    }

    [Fact]
    public async Task ReturnsDefaultWhenNoScheduleExists()
    {
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(Array.Empty<SensitivitySchedule>());

        var result = await _sut.GetSensitivityAsync(NoonMills);

        result.Should().Be(50.0);
    }

    [Fact]
    public async Task UsesActiveProfileNameWhenSpecProfileIsNull()
    {
        _activeProfileResolver.Setup(r => r.GetActiveProfileNameAsync(NoonMills, default))
            .ReturnsAsync("Night");
        var schedule = MakeSchedule((0, 75.0));
        _repo.Setup(r => r.GetByProfileNameAsync("Night", default))
            .ReturnsAsync(new[] { schedule });

        var result = await _sut.GetSensitivityAsync(NoonMills);

        result.Should().Be(75.0);
    }

    [Fact]
    public async Task UsesSpecProfileWhenProvided()
    {
        var schedule = MakeSchedule((0, 100.0));
        _repo.Setup(r => r.GetByProfileNameAsync("Custom", default))
            .ReturnsAsync(new[] { schedule });

        var result = await _sut.GetSensitivityAsync(NoonMills, specProfile: "Custom");

        result.Should().Be(100.0);
        _activeProfileResolver.Verify(r => r.GetActiveProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // NoonMills (2024-01-15 12:00 UTC) maps to 07:00 in America/New_York (EST), selecting the
    // pre-12:00 schedule value (40.0); UTC keeps it at noon (60.0). So the chosen timezone source
    // is observable in the result.

    [Fact]
    public async Task UsesCanonicalPatientTimezone_OverLegacyTherapySettingsTimezone()
    {
        var schedule = MakeSchedule((0, 40.0), (12 * 3600, 60.0));
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(new[] { schedule });
        _therapyRepo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync(new TherapySettings { Timezone = "UTC" });
        _patientRecordRepo.Setup(r => r.GetAsync(default))
            .ReturnsAsync(new PatientRecord { Timezone = "America/New_York" });

        var result = await _sut.GetSensitivityAsync(NoonMills);

        result.Should().Be(40.0);
    }

    [Fact]
    public async Task FallsBackToTherapySettingsTimezone_WhenPatientRecordHasNone()
    {
        var schedule = MakeSchedule((0, 40.0), (12 * 3600, 60.0));
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(new[] { schedule });
        _therapyRepo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync(new TherapySettings { Timezone = "America/New_York" });
        _patientRecordRepo.Setup(r => r.GetAsync(default))
            .ReturnsAsync(new PatientRecord { Timezone = null });

        var result = await _sut.GetSensitivityAsync(NoonMills);

        result.Should().Be(40.0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LookupsAcrossAScheduleEditEachGetTheirOwnVersion(bool earlyLookupFirst)
    {
        var olderVersion = MakeSchedule((0, 40.0));
        olderVersion.Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(NoonMills).UtcDateTime.AddDays(-1);
        var editedVersion = MakeSchedule((0, 60.0));
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
            early = await _sut.GetSensitivityAsync(beforeEdit);
            late = await _sut.GetSensitivityAsync(NoonMills);
        }
        else
        {
            late = await _sut.GetSensitivityAsync(NoonMills);
            early = await _sut.GetSensitivityAsync(beforeEdit);
        }

        early.Should().Be(40.0);
        late.Should().Be(60.0);
    }
}
