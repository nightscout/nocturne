using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Profiles.Resolvers;

public class ProfileVersionCacheTests : IDisposable
{
    private readonly Mock<IBasalScheduleRepository> _repo = new();
    private readonly MemoryCache _memoryCache = new(new MemoryCacheOptions());
    private readonly ProfileVersionCache _sut;

    private static readonly DateTime Day1 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day5 = Day1.AddDays(4);
    private static readonly DateTime Day9 = Day1.AddDays(8);

    private readonly BasalSchedule _first = Version(Day1);
    private readonly BasalSchedule _second = Version(Day5);

    public ProfileVersionCacheTests()
    {
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(new[] { _second, _first });
        _sut = new ProfileVersionCache(TestTenantCache.Create(_memoryCache));
    }

    public void Dispose() => _memoryCache.Dispose();

    private static BasalSchedule Version(DateTime timestamp) => new()
    {
        Id = Guid.NewGuid(),
        ProfileName = "Default",
        Timestamp = timestamp,
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReturnsTheVersionActiveAtEachTimeWhateverWasLookedUpFirst(bool earlyLookupFirst)
    {
        BasalSchedule? early, late;
        if (earlyLookupFirst)
        {
            early = await _sut.GetActiveAtAsync(_repo.Object, "Default", Day1.AddDays(1), default);
            late = await _sut.GetActiveAtAsync(_repo.Object, "Default", Day9, default);
        }
        else
        {
            late = await _sut.GetActiveAtAsync(_repo.Object, "Default", Day9, default);
            early = await _sut.GetActiveAtAsync(_repo.Object, "Default", Day1.AddDays(1), default);
        }

        early.Should().BeSameAs(_first);
        late.Should().BeSameAs(_second);
    }

    [Fact]
    public async Task ReturnsTheVersionStartingExactlyAtTheTime()
    {
        var result = await _sut.GetActiveAtAsync(_repo.Object, "Default", Day5, default);

        result.Should().BeSameAs(_second);
    }

    [Fact]
    public async Task ReadsTheNewestVersionBeforeTheFirst_AsNightscoutReadsItsNewestDocument()
    {
        var result = await _sut.GetActiveAtAsync(_repo.Object, "Default", Day1.AddSeconds(-1), default);

        result.Should().BeSameAs(_second);
    }

    [Fact]
    public async Task ReturnsNullForAProfileWithNoVersion()
    {
        var result = await _sut.GetActiveAtAsync(_repo.Object, "Missing", Day9, default);

        result.Should().BeNull();
    }

    [Fact]
    public async Task LoadsTheVersionsOncePerProfileWithinTheTtl()
    {
        await _sut.GetActiveAtAsync(_repo.Object, "Default", Day1, default);
        await _sut.GetActiveAtAsync(_repo.Object, "Default", Day9, default);

        _repo.Verify(r => r.GetByProfileNameAsync("Default", default), Times.Once);
    }

    [Fact]
    public async Task KeepsTenantsApart()
    {
        await _sut.GetActiveAtAsync(_repo.Object, "Default", Day1, default);
        var otherTenant = new ProfileVersionCache(TestTenantCache.Create(_memoryCache, tenantId: Guid.NewGuid()));

        await otherTenant.GetActiveAtAsync(_repo.Object, "Default", Day1, default);

        _repo.Verify(r => r.GetByProfileNameAsync("Default", default), Times.Exactly(2));
    }

    [Theory]
    [MemberData(nameof(NarrowedReaders.All), MemberType = typeof(NarrowedReaders))]
    public async Task ANarrowedReaderNeverSharesTheOwnersVersions(string reader)
    {
        var narrowed = new ProfileVersionCache(NarrowedReaders.Cache(reader, _memoryCache));

        await _sut.GetActiveAtAsync(_repo.Object, "Default", Day9, default);
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default)).ReturnsAsync([]);
        var narrowedVersion = await narrowed.GetActiveAtAsync(_repo.Object, "Default", Day9, default);
        var ownerVersion = await _sut.GetActiveAtAsync(_repo.Object, "Default", Day9, default);

        narrowedVersion.Should().BeNull("the owner's cached versions are not served");
        ownerVersion.Should().BeSameAs(_second, "the narrowed reader's empty list is not written over them");
    }
}
