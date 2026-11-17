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

public class BasalRateResolverTests : IDisposable
{
    private readonly Mock<IBasalScheduleRepository> _repo = new();
    private readonly Mock<ITherapySettingsRepository> _therapyRepo = new();
    private readonly Mock<IPatientRecordRepository> _patientRecordRepo = new();
    private readonly Mock<IActiveProfileResolver> _activeProfileResolver = ActiveProfileResolverMock.WithDefault();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly BasalRateResolver _sut;


    // 2024-01-15 12:00:00 UTC
    private const long NoonMills = 1705320000000;

    public BasalRateResolverTests()
    {

        _sut = new BasalRateResolver(
            _repo.Object,
            _therapyRepo.Object,
            _patientRecordRepo.Object,
            _activeProfileResolver.Object,
            new ProfileVersionCache(TestTenantCache.Create(_cache)),
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
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(new[] { schedule });

        var result = await _sut.GetBasalRateAsync(NoonMills);

        result.Should().Be(1.0);
    }

    [Fact]
    public async Task AppliesCcpPercentageScaling()
    {
        var schedule = MakeSchedule((0, 1.0));
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(new[] { schedule });
        _activeProfileResolver.Setup(r => r.GetCircadianAdjustmentAsync(NoonMills, default))
            .ReturnsAsync(new CircadianAdjustment(150, 0));

        var result = await _sut.GetBasalRateAsync(NoonMills);

        result.Should().Be(1.5);
    }

    [Fact]
    public async Task ReturnsDefaultWhenNoScheduleExists()
    {
        _repo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(Array.Empty<BasalSchedule>());

        var result = await _sut.GetBasalRateAsync(NoonMills);

        result.Should().Be(1.0);
    }

    [Fact]
    public async Task UsesActiveProfileNameWhenSpecProfileIsNull()
    {
        _activeProfileResolver.Setup(r => r.GetActiveProfileNameAsync(NoonMills, default))
            .ReturnsAsync("Weekday");
        var schedule = MakeSchedule((0, 2.0));
        _repo.Setup(r => r.GetByProfileNameAsync("Weekday", default))
            .ReturnsAsync(new[] { schedule });

        var result = await _sut.GetBasalRateAsync(NoonMills);

        result.Should().Be(2.0);
    }

    [Fact]
    public async Task UsesSpecProfileWhenProvided()
    {
        var schedule = MakeSchedule((0, 3.0));
        _repo.Setup(r => r.GetByProfileNameAsync("Custom", default))
            .ReturnsAsync(new[] { schedule });

        var result = await _sut.GetBasalRateAsync(NoonMills, specProfile: "Custom");

        result.Should().Be(3.0);
        _activeProfileResolver.Verify(r => r.GetActiveProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public class BuildResolverAsync : BasalRateResolverTests
    {
        // 2024-01-15 00:00:00 UTC (midnight)
        private const long MidnightMills = 1705276800000;
        // 2024-01-15 23:59:59 UTC (end of day)
        private const long EndOfDayMills = MidnightMills + 86_399_999;

        private void SetupSpans(params ProfileSpan[] spans)
        {
            _activeProfileResolver
                .Setup(r => r.GetProfileSpansForRangeAsync(
                    It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(spans.ToList());
        }

        [Fact]
        public async Task NoSpans_ReturnsDefaultRateEverywhere()
        {
            SetupSpans();

            var rateAt = await _sut.BuildResolverAsync(MidnightMills, EndOfDayMills);

            rateAt(MidnightMills).Should().Be(1.0);
            rateAt(MidnightMills + 43_200_000).Should().Be(1.0); // noon
            rateAt(EndOfDayMills).Should().Be(1.0);
        }

        [Fact]
        public async Task SingleSpanSingleProfile_ReturnsScheduledRate()
        {
            // Flat schedule: 1.2 U/hr all day
            var schedule = MakeSchedule((0, 1.2));
            _repo.Setup(r => r.GetByProfileNameAsync("Default", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { schedule });

            SetupSpans(new ProfileSpan("Default", MidnightMills - 3_600_000, null, null));

            var rateAt = await _sut.BuildResolverAsync(MidnightMills, EndOfDayMills);

            rateAt(MidnightMills).Should().Be(1.2);
            rateAt(MidnightMills + 43_200_000).Should().Be(1.2); // noon
        }

        [Fact]
        public async Task ProfileSwitch_ReturnsDifferentRatesEitherSide()
        {
            var morningSchedule = MakeSchedule((0, 0.8));
            var afternoonSchedule = MakeSchedule((0, 1.4));

            _repo.Setup(r => r.GetByProfileNameAsync("Morning", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { morningSchedule });
            _repo.Setup(r => r.GetByProfileNameAsync("Afternoon", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { afternoonSchedule });

            // Switch at noon
            var noonMills = MidnightMills + 43_200_000;
            SetupSpans(
                new ProfileSpan("Morning",   MidnightMills - 3_600_000, noonMills, null),
                new ProfileSpan("Afternoon", noonMills,                 null,      null));

            var rateAt = await _sut.BuildResolverAsync(MidnightMills, EndOfDayMills);

            rateAt(MidnightMills + 3_600_000).Should().Be(0.8);  // 1am — Morning
            rateAt(noonMills + 3_600_000).Should().Be(1.4);      // 1pm — Afternoon
        }

        [Fact]
        public async Task CircadianAdjustment_ScalesRate()
        {
            var schedule = MakeSchedule((0, 1.0));
            _repo.Setup(r => r.GetByProfileNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { schedule });

            SetupSpans(new ProfileSpan("Default", MidnightMills - 3_600_000, null,
                new CircadianAdjustment(Percentage: 150, TimeshiftMs: 0)));

            var rateAt = await _sut.BuildResolverAsync(MidnightMills, EndOfDayMills);

            rateAt(MidnightMills).Should().Be(1.5);
        }

        [Fact]
        public async Task IssuesOneSpanQuery_RegardlessOfDelegateCallCount()
        {
            SetupSpans();

            var rateAt = await _sut.BuildResolverAsync(MidnightMills, EndOfDayMills);
            for (int i = 0; i < 288; i++)
                rateAt(MidnightMills + i * 300_000L);

            _activeProfileResolver.Verify(
                r => r.GetProfileSpansForRangeAsync(
                    It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task BoundaryTimestamp_AtSpanStartIsIncluded()
        {
            var schedule = MakeSchedule((0, 2.0));
            _repo.Setup(r => r.GetByProfileNameAsync("Profile2", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { schedule });
            // "Default" is NOT set up — returns null → DefaultBasalRate fallback

            var spanStart = MidnightMills + 3_600_000;
            SetupSpans(
                new ProfileSpan("Default", MidnightMills, spanStart, null),
                new ProfileSpan("Profile2", spanStart, EndOfDayMills, null));

            var rateAt = await _sut.BuildResolverAsync(MidnightMills, EndOfDayMills);

            rateAt(spanStart).Should().Be(2.0);      // exactly at start — Profile2 active
            rateAt(spanStart - 1).Should().Be(1.0);  // one ms before — Default, whose schedule is null → DefaultBasalRate
        }

        [Fact]
        public async Task AScheduleVersionInsideTheRange_AppliesFromItsOwnStart()
        {
            var noonMills = MidnightMills + 43_200_000;
            var older = MakeSchedule((0, 0.8));
            older.Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(MidnightMills).UtcDateTime.AddDays(-1);
            var newer = MakeSchedule((0, 1.4));
            newer.Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(noonMills).UtcDateTime;
            _repo.Setup(r => r.GetByProfileNameAsync("Default", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { newer, older });
            SetupSpans(new ProfileSpan("Default", MidnightMills, EndOfDayMills, null));

            var rateAt = await _sut.BuildResolverAsync(MidnightMills, EndOfDayMills);

            rateAt(MidnightMills + 3_600_000).Should().Be(0.8);
            rateAt(noonMills + 3_600_000).Should().Be(1.4);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LookupsAcrossAScheduleEditEachGetTheirOwnVersion(bool earlyLookupFirst)
    {
        var olderVersion = MakeSchedule((0, 1.0));
        olderVersion.Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(NoonMills).UtcDateTime.AddDays(-1);
        var editedVersion = MakeSchedule((0, 2.0));
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
            early = await _sut.GetBasalRateAsync(beforeEdit);
            late = await _sut.GetBasalRateAsync(NoonMills);
        }
        else
        {
            late = await _sut.GetBasalRateAsync(NoonMills);
            early = await _sut.GetBasalRateAsync(beforeEdit);
        }

        early.Should().Be(1.0);
        late.Should().Be(2.0);
    }
}
