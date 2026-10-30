using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Analytics;

public class LoopAnalysisRequestResolverTests
{
    // 18:20 on 2024-06-10 in Sydney (UTC+10, no DST in June); the clamp window starts at 18:20 on 9 June.
    private static readonly DateTime Now = new(2024, 6, 10, 8, 20, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowStart = Now.AddHours(-24);

    private static readonly HashSet<string> Granted =
        [Scope.ReportsRead, Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead, Scope.TherapyRead];

    private static readonly HashSet<string> DatesAndComparison = new(StringComparer.OrdinalIgnoreCase)
    {
        "startDate", "endDate", "compare",
    };

    private static LoopAnalysisRequestResolver Resolver()
    {
        var settings = new Mock<ITherapySettingsResolver>();
        settings.Setup(s => s.GetTimezoneAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync("Australia/Sydney");
        var clock = new TenantClockResolver(settings.Object, new CategoryReadContext(), NullLogger<TenantClockResolver>.Instance);
        return new LoopAnalysisRequestResolver(clock, new FakeTimeProvider(new DateTimeOffset(Now)));
    }

    private static LoopAnalysisQuery Query(int startDay, int endDay) => new()
    {
        StartDate = new DateOnly(2024, 6, startDay),
        EndDate = new DateOnly(2024, 6, endDay),
        Compare = LoopAnalysisComparison.Previous,
    };

    [Fact]
    public async Task Clamped_ARangeInsideTheWindow_IsUnchanged_AndNoDateIsIgnored()
    {
        var resolved = await Resolver().ResolveAsync(Query(10, 10), Granted, true, DatesAndComparison, CancellationToken.None);

        resolved.PrimaryRange!.From.Should().Be(new DateTime(2024, 6, 9, 14, 0, 0, DateTimeKind.Utc), "midnight on 10 June, local");
        resolved.PrimaryRange.To.Should().Be(Now);
        resolved.IgnoredFilters.Should().Equal("compare");
        resolved.HistoryClamped.Should().BeTrue();
        resolved.RangeOutsideHistory.Should().BeFalse();
        resolved.ComparisonRange.Should().BeNull();
        resolved.Filter!.HistoryVisibleFrom.Should().Be(WindowStart);
        resolved.Filter.CompareFrom.Should().BeNull();
    }

    [Fact]
    public async Task Clamped_ARangeStartingBeforeTheWindow_IsCutAtItsStart_AndStartDateIsIgnored()
    {
        var resolved = await Resolver().ResolveAsync(Query(3, 10), Granted, true, DatesAndComparison, CancellationToken.None);

        resolved.PrimaryRange!.From.Should().Be(WindowStart);
        resolved.PrimaryRange.To.Should().Be(Now);
        resolved.PrimaryRange.StartDate.Should().Be(new DateOnly(2024, 6, 9));
        resolved.PrimaryRange.EndDate.Should().Be(new DateOnly(2024, 6, 10));
        resolved.IgnoredFilters.Should().Equal("startDate", "compare");
        resolved.RangeOutsideHistory.Should().BeFalse();
        resolved.Filter!.From.Should().Be(WindowStart);
        resolved.Filter.HistoryVisibleFrom.Should().Be(WindowStart);
    }

    [Fact]
    public async Task Clamped_ARangeEndingBeforeTheWindow_IsEmptyAtItsStart_AndSaysSo()
    {
        var resolved = await Resolver().ResolveAsync(Query(1, 3), Granted, true, DatesAndComparison, CancellationToken.None);

        resolved.RangeOutsideHistory.Should().BeTrue();
        resolved.HistoryClamped.Should().BeTrue();
        resolved.PrimaryRange!.From.Should().Be(WindowStart);
        resolved.PrimaryRange.To.Should().Be(WindowStart);
        resolved.Filter.Should().BeNull("there is nothing to analyse");
        resolved.IgnoredFilters.Should().Equal("startDate", "endDate", "compare");
        resolved.ComparisonRange.Should().BeNull();
    }

    [Fact]
    public async Task Clamped_AskedInInstants_IsCutToThemInsideTheWindow()
    {
        var resolved = await Resolver().ResolveAsync(
            Query(10, 10), Granted, true, new HashSet<string>(), CancellationToken.None, (Now.AddHours(-6), Now));

        resolved.PrimaryRange!.From.Should().Be(Now.AddHours(-6));
        resolved.PrimaryRange.To.Should().Be(Now);
        resolved.Filter!.HistoryVisibleFrom.Should().Be(WindowStart, "the edge of what the caller may read is the window's, not the request's");
        resolved.IgnoredFilters.Should().BeEmpty();
    }

    [Fact]
    public async Task Unclamped_IsReadOverTheWholeLocalDays_WithItsComparison()
    {
        var resolved = await Resolver().ResolveAsync(
            Query(3, 10), Granted, false, DatesAndComparison, CancellationToken.None, (Now.AddHours(-6), Now));

        resolved.PrimaryRange!.From.Should().Be(new DateTime(2024, 6, 2, 14, 0, 0, DateTimeKind.Utc));
        resolved.PrimaryRange.To.Should().Be(Now);
        resolved.ComparisonRange.Should().NotBeNull();
        resolved.IgnoredFilters.Should().BeEmpty();
        resolved.HistoryClamped.Should().BeFalse();
        resolved.RangeOutsideHistory.Should().BeFalse();
        resolved.Filter!.HistoryVisibleFrom.Should().BeNull();
    }
}
