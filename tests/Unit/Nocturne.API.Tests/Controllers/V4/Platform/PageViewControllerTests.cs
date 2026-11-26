using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Services.Telemetry;
using Nocturne.API.Tests.TestDoubles;

namespace Nocturne.API.Tests.Controllers.V4.Platform;

[Trait("Category", "Unit")]
public class PageViewControllerTests
{
    private static PageViewController Controller(PageViewCounter counter) =>
        new(counter)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            ProblemDetailsFactory = new EchoingProblemDetailsFactory(),
        };

    private static PageViewCounter Counter(bool enabled) =>
        new(Options.Create(new PageCountsOptions { Enabled = enabled }), new FakeTimeProvider());

    [Fact]
    public void While_page_counts_are_off_a_beacon_has_no_effect()
    {
        var counter = Counter(enabled: false);

        var result = Controller(counter).RecordPageView(new PageViewRequest("/(authenticated)/reports/agp"));

        result.Should().BeOfType<NoContentResult>();
        counter.Drain().Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_route_id_is_refused()
    {
        var counter = Counter(enabled: true);

        var result = Controller(counter).RecordPageView(new PageViewRequest("/(authenticated)/reports/agp/../../admin"));

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        counter.Drain().Should().BeEmpty();
    }

    [Fact]
    public void A_known_route_id_is_counted()
    {
        var counter = Counter(enabled: true);

        var result = Controller(counter).RecordPageView(new PageViewRequest("/(authenticated)/reports/agp"));

        result.Should().BeOfType<NoContentResult>();
        counter.Drain().Should().ContainSingle().Which.RouteId.Should().Be("/(authenticated)/reports/agp");
    }
}
