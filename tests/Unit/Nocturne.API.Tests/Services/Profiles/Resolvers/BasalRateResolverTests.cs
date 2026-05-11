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

    public class BuildResolverAsync : BasalRateResolverTests
    {
        // 2024-01-15 00:00:00 UTC (midnight)
        private const long MidnightMills = 1705276800000;
        // 2024-01-15 23:59:59 UTC (end of day)
        private const long EndOfDayMills = MidnightMills + 86_399_999;

        private void SetupSpans(params ProfileSpan[] spans)
        {
            _activeProfileResolver
                .Setup(r => r.GetActiveProfileSpansForRangeAsync(
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
            _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(schedule);

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

            _repo.Setup(r => r.GetActiveAtAsync("Morning", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(morningSchedule);
            _repo.Setup(r => r.GetActiveAtAsync("Afternoon", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(afternoonSchedule);

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
            _repo.Setup(r => r.GetActiveAtAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(schedule);

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
                r => r.GetActiveProfileSpansForRangeAsync(
                    It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task BoundaryTimestamp_AtSpanStartIsIncluded()
        {
            var schedule = MakeSchedule((0, 2.0));
            _repo.Setup(r => r.GetActiveAtAsync("Profile2", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(schedule);
            // "Default" is NOT set up — returns null → DefaultBasalRate fallback

            var spanStart = MidnightMills + 3_600_000;
            SetupSpans(new ProfileSpan("Profile2", spanStart, null, null));

            var rateAt = await _sut.BuildResolverAsync(MidnightMills, EndOfDayMills);

            rateAt(spanStart).Should().Be(2.0);      // exactly at start — Profile2 active
            rateAt(spanStart - 1).Should().Be(1.0);  // one ms before — no span, Default schedule null → DefaultBasalRate
        }
    }
}
