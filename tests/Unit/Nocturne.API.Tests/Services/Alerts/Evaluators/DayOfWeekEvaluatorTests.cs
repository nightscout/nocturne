using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class DayOfWeekEvaluatorTests
{
    [Fact]
    public void ConditionType_ShouldBeDayOfWeek()
    {
        var sut = new DayOfWeekEvaluator(new FakeTimeProvider());
        sut.ConditionType.Should().Be(AlertConditionType.DayOfWeek);
    }

    [Fact]
    public async Task NullDays_ReturnsFalse()
    {
        var sut = MakeSut(new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc));
        var json = """{"days": []}""";
        var ctx = MakeContext(timezone: null);

        (await sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task UtcSundayMatchesSunday()
    {
        // 2026-03-22 is a Sunday in UTC.
        var sut = MakeSut(new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc));
        var json = """{"days": ["Sunday"]}""";
        var ctx = MakeContext(timezone: null);

        (await sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task PacificAuckland_LateSundayUtc_IsMondayLocal()
    {
        // 2026-03-22 23:30 UTC = 2026-03-23 12:30 NZDT (UTC+13 in March, DST active in NZ).
        var sut = MakeSut(new DateTime(2026, 3, 22, 23, 30, 0, DateTimeKind.Utc));
        var json = """{"days": ["Monday"]}""";
        var ctx = MakeContext(timezone: TryResolve("Pacific/Auckland", "New Zealand Standard Time"));

        (await sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task NewYork_DstTransition_LocalDayResolves()
    {
        // 2026-03-08 is the DST start in America/New_York. At 06:30 UTC the local time is
        // 02:30 EDT (post-spring-forward), still Sunday.
        var sut = MakeSut(new DateTime(2026, 3, 8, 6, 30, 0, DateTimeKind.Utc));
        var jsonSunday = """{"days": ["Sunday"]}""";
        var jsonMonday = """{"days": ["Monday"]}""";
        var ctx = MakeContext(timezone: TryResolve("America/New_York", "Eastern Standard Time"));

        (await sut.EvaluateAsync(jsonSunday, ctx, CancellationToken.None)).Should().BeTrue();
        (await sut.EvaluateAsync(jsonMonday, ctx, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task UnknownTimezone_FallsBackToUtc()
    {
        var sut = MakeSut(new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc));
        var json = """{"days": ["Sunday"]}""";
        var ctx = MakeContext(timezone: "Mars/Olympus_Mons");

        (await sut.EvaluateAsync(json, ctx, CancellationToken.None)).Should().BeTrue();
    }

    private static DayOfWeekEvaluator MakeSut(DateTime fixedNowUtc)
    {
        return new DayOfWeekEvaluator(new FakeTimeProvider(new DateTimeOffset(fixedNowUtc)));
    }

    private static SensorContext MakeContext(string? timezone) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
        TrendRate = 0m,
        LastReadingAt = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
        TenantTimeZoneId = timezone,
    };

    /// <summary>
    /// Some test hosts expose IANA names, others Windows names. Resolve to whichever the host
    /// recognises so the test passes on both Windows CI and Linux CI.
    /// </summary>
    private static string TryResolve(string ianaId, string windowsId)
    {
        try { TimeZoneInfo.FindSystemTimeZoneById(ianaId); return ianaId; }
        catch { return windowsId; }
    }
}
