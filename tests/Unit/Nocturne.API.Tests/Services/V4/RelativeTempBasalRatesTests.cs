using FluentAssertions;
using Moq;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

public class RelativeTempBasalRatesTests
{
    private readonly Mock<IBasalRateResolver> _basalRates = new();
    private readonly Mock<ITempBasalRepository> _tempBasals = new();
    private readonly Mock<IBasalScheduleRepository> _basalSchedules = new();

    public RelativeTempBasalRatesTests()
    {
        _basalSchedules
            .Setup(r => r.GetByProfileNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private static TempBasal Unresolved(long startMills, double percent) => new()
    {
        Id = Guid.CreateVersion7(),
        StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startMills).UtcDateTime,
        Percent = percent,
    };

    [Fact]
    public async Task ResolvePendingAsync_ResolvesTheTempsANewScheduleCovers_AndLeavesTheRest()
    {
        var covered = Unresolved(1_700_000_000_000, -50);
        var uncovered = Unresolved(1_600_000_000_000, -50);
        _tempBasals
            .Setup(r => r.GetUnresolvedRelativeAsync(null, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([uncovered, covered]);
        _basalRates
            .Setup(r => r.FindBasalRateAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long mills, CancellationToken _) => mills >= 1_650_000_000_000 ? 0.8 : null);
        var updated = new List<TempBasal>();
        _tempBasals
            .Setup(r => r.UpdateAsync(It.IsAny<Guid>(), It.IsAny<TempBasal>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, TempBasal tb, WriteOrigin _, CancellationToken _) => updated.Add(tb))
            .ReturnsAsync((Guid _, TempBasal tb, WriteOrigin _, CancellationToken _) => tb);

        var resolved = await TempBasalTestDeps.Rates(_basalRates.Object, _tempBasals.Object, _basalSchedules.Object)
            .ResolvePendingAsync(["Default"], CancellationToken.None);

        resolved.Should().Be(1);
        updated.Should().ContainSingle().Which.Should().BeSameAs(covered);
        covered.Rate.Should().BeApproximately(0.4, 1e-9);
        covered.ScheduledRate.Should().Be(0.8);
        covered.RateFromSchedule.Should().BeTrue();
        uncovered.Rate.Should().BeNull();
        _basalSchedules.Verify(r => r.GetByProfileNameAsync("Default", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolvePendingAsync_PagesPastTheTempsItCannotResolve()
    {
        var firstPage = Enumerable.Range(0, 500).Select(i => Unresolved(1_600_000_000_000 + i, -50)).ToList();
        var last = firstPage[^1];
        var secondPage = new List<TempBasal> { Unresolved(1_700_000_000_000, -50) };
        (DateTime, Guid)? cursor = (last.StartTimestamp, last.Id);
        _tempBasals
            .Setup(r => r.GetUnresolvedRelativeAsync(null, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(firstPage);
        _tempBasals
            .Setup(r => r.GetUnresolvedRelativeAsync(cursor, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(secondPage);
        _basalRates
            .Setup(r => r.FindBasalRateAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long mills, CancellationToken _) => mills >= 1_650_000_000_000 ? 1.0 : null);
        _tempBasals
            .Setup(r => r.UpdateAsync(It.IsAny<Guid>(), It.IsAny<TempBasal>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, TempBasal tb, WriteOrigin _, CancellationToken _) => tb);

        var resolved = await TempBasalTestDeps.Rates(_basalRates.Object, _tempBasals.Object, _basalSchedules.Object)
            .ResolvePendingAsync([], CancellationToken.None);

        resolved.Should().Be(1);
        secondPage[0].Rate.Should().BeApproximately(0.5, 1e-9);
    }
}
