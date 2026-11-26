using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Telemetry;
using Nocturne.Core.Contracts.Telemetry;

namespace Nocturne.API.Tests.Services.Telemetry;

[Trait("Category", "Unit")]
public class PageViewCounterTests
{
    private const string Agp = "/(authenticated)/reports/agp";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 27, 23, 59, 0, TimeSpan.Zero));

    private PageViewCounter Counter(bool enabled) =>
        new(Options.Create(new PageCountsOptions { Enabled = enabled }), _clock);

    [Fact]
    public void Counts_are_off_by_default()
    {
        new PageCountsOptions().Enabled.Should().BeFalse();
    }

    [Fact]
    public void A_view_is_not_counted_while_page_counts_are_off()
    {
        var counter = Counter(enabled: false);

        counter.Record(Agp).Should().BeFalse();

        counter.Drain().Should().BeEmpty();
    }

    [Theory]
    [InlineData("/(authenticated)/reports/nope")]
    [InlineData("/reports/agp")]
    [InlineData("/(authenticated)/reports/agp?from=1")]
    [InlineData("")]
    public void An_unknown_route_id_is_not_counted(string routeId)
    {
        var counter = Counter(enabled: true);

        counter.Record(routeId).Should().BeFalse();

        counter.Drain().Should().BeEmpty();
    }

    [Fact]
    public void Views_are_summed_by_utc_day_and_route()
    {
        var counter = Counter(enabled: true);

        counter.Record(Agp);
        counter.Record(Agp);
        counter.Record("/(authenticated)/calendar");
        _clock.Advance(TimeSpan.FromMinutes(2));
        counter.Record(Agp);

        counter.Drain().Should().BeEquivalentTo(new[]
        {
            new PageViewCount(new DateOnly(2026, 11, 27), Agp, 2),
            new PageViewCount(new DateOnly(2026, 11, 27), "/(authenticated)/calendar", 1),
            new PageViewCount(new DateOnly(2026, 11, 28), Agp, 1),
        });
        counter.Drain().Should().BeEmpty();
    }

    [Fact]
    public void Restored_counts_add_to_those_counted_since()
    {
        var counter = Counter(enabled: true);
        counter.Record(Agp);
        var drained = counter.Drain();

        counter.Record(Agp);
        counter.Restore(drained);

        counter.Drain().Should().ContainSingle()
            .Which.Should().Be(new PageViewCount(new DateOnly(2026, 11, 27), Agp, 2));
    }
}
