using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Profiles.Resolvers;

/// <summary>The schedule versions <see cref="BasalSegmentService"/> reads across a window.</summary>
public sealed class BasalSegmentServiceTests : IDisposable
{
    private const long Hour = 3_600_000;
    private const long From = 1_705_320_000_000;
    private const long To = From + 4 * Hour;

    private readonly Mock<IActiveProfileResolver> _activeProfile = ActiveProfileResolverMock.WithDefault();
    private readonly Mock<IBasalScheduleRepository> _schedules = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public BasalSegmentServiceTests() =>
        _activeProfile
            .Setup(a => a.GetProfileSpansForRangeAsync(From, To, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ProfileSpan("Default", From, To, null)]);

    public void Dispose() => _cache.Dispose();

    private static BasalSchedule Version(long fromMills, double rate) => new()
    {
        ProfileName = "Default",
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(fromMills).UtcDateTime,
        Entries = [new ScheduleEntry { Time = "00:00", TimeAsSeconds = 0, Value = rate }],
    };

    [Fact]
    public async Task BeforeTheFirstScheduleVersion_TheNewestIsRead()
    {
        _schedules.Setup(r => r.GetByProfileNameAsync("Default", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Version(From + 2 * Hour, 1.2), Version(From + Hour, 0.8)]);
        var service = new BasalSegmentService(
            _activeProfile.Object,
            _schedules.Object,
            Mock.Of<ITherapySettingsRepository>(),
            Mock.Of<IPatientRecordRepository>(),
            TestTenantCache.Create(_cache));

        var segments = new List<BasalSegment>();
        await foreach (var segment in service.GetSegmentsAsync(From, To))
            segments.Add(segment);

        segments.Where(s => s.StartMills < From + Hour)
            .Should().NotBeEmpty().And.OnlyContain(s => s.UnitsPerHour == 1.2);
        segments.Where(s => s.StartMills >= From + Hour && s.StartMills < From + 2 * Hour)
            .Should().NotBeEmpty().And.OnlyContain(s => s.UnitsPerHour == 0.8);
        segments.Where(s => s.StartMills >= From + 2 * Hour)
            .Should().NotBeEmpty().And.OnlyContain(s => s.UnitsPerHour == 1.2);
    }
}
