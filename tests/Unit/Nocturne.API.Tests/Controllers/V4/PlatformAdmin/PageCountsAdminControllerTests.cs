using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Controllers.V4.PlatformAdmin;
using Nocturne.API.Services.Telemetry;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Telemetry;

namespace Nocturne.API.Tests.Controllers.V4.PlatformAdmin;

[Trait("Category", "Unit")]
public class PageCountsAdminControllerTests
{
    private static readonly DateOnly Today = new(2026, 11, 27);

    private readonly Mock<IPageViewCountStore> _store = new();

    private PageCountsAdminController Controller() =>
        new(_store.Object, new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero)))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            ProblemDetailsFactory = new EchoingProblemDetailsFactory(),
        };

    [Fact]
    public async Task Without_a_range_it_reads_the_last_thirty_utc_days()
    {
        _store.Setup(s => s.ReadAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PageViewCount(Today, "/(authenticated)/calendar", 3)]);

        var result = await Controller().GetPageCounts();

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeEquivalentTo(
            new[] { new PageViewCount(Today, "/(authenticated)/calendar", 3) });
        _store.Verify(s => s.ReadAsync(Today.AddDays(-29), Today, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void The_route_list_is_every_known_page_in_ordinal_order()
    {
        var result = Controller().GetPageRoutes();

        var routes = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeAssignableTo<List<string>>().Subject;
        routes.Should().BeEquivalentTo(PageRouteIds.All).And.BeInAscendingOrder(StringComparer.Ordinal);
        routes.Should().Contain("/(authenticated)");
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_range_that_ends_before_it_starts_is_refused()
    {
        var result = await Controller().GetPageCounts(from: Today, to: Today.AddDays(-1));

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        _store.VerifyNoOtherCalls();
    }
}
