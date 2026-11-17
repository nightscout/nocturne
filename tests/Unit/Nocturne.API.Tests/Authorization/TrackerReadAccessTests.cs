using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Widget;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Pins <see cref="TrackerReadAccess"/> and the two analytics payloads that carry trackers outside
/// the tracker endpoints: the dashboard chart's tracker markers and the widget summary's statuses.
/// </summary>
[Trait("Category", "Unit")]
public class TrackerReadAccessTests
{
    public enum Caller { MemberWithAlertsRead, MemberWithGlucoseOnly, Guest, Share }

    private static HttpContext ContextFor(Caller caller)
    {
        var httpContext = new DefaultHttpContext();
        var scopes = new HashSet<string> { Scope.GlucoseRead };
        switch (caller)
        {
            case Caller.MemberWithAlertsRead:
                scopes.Add(Scope.AlertsRead);
                httpContext.Items["AuthContext"] = Member();
                break;
            case Caller.MemberWithGlucoseOnly:
                httpContext.Items["AuthContext"] = Member();
                break;
            case Caller.Guest:
                scopes.Add(Scope.AlertsRead);
                httpContext.Items["AuthContext"] = new AuthContext
                {
                    IsAuthenticated = true,
                    AuthType = AuthType.Guest,
                    ActingAsSubjectId = Guid.NewGuid(),
                };
                break;
            case Caller.Share:
                scopes.Add(Scope.AlertsRead);
                httpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = false };
                httpContext.SetShareAccess();
                break;
        }

        httpContext.Items["GrantedScopes"] = scopes;
        return httpContext;
    }

    private static AuthContext Member() => new()
    {
        IsAuthenticated = true,
        AuthType = AuthType.SessionCookie,
        SubjectId = Guid.NewGuid(),
    };

    [Theory]
    [InlineData(Scope.AlertsRead)]
    [InlineData(Scope.AlertsReadWrite)]
    [InlineData(Scope.FullAccess)]
    public void AMemberHoldingAnAlertsReadScope_ReadsTrackers(string scope)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = Member();
        httpContext.Items["GrantedScopes"] = new HashSet<string> { scope };

        TrackerReadAccess.Allows(httpContext).Should().BeTrue();
    }

    [Theory]
    [InlineData(Caller.MemberWithAlertsRead, true)]
    [InlineData(Caller.MemberWithGlucoseOnly, false)]
    [InlineData(Caller.Guest, false)]
    [InlineData(Caller.Share, false)]
    public void Allows_OnlyAnAccountHoldingAlertsRead(Caller caller, bool allowed)
    {
        TrackerReadAccess.Allows(ContextFor(caller)).Should().Be(allowed);
    }

    [Fact]
    public void ACallerWithNoAuthContext_ReadsNoTrackers()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = new HashSet<string> { Scope.AlertsRead };

        TrackerReadAccess.Allows(httpContext).Should().BeFalse();
    }

    [Theory]
    [InlineData(Caller.MemberWithAlertsRead, 1)]
    [InlineData(Caller.MemberWithGlucoseOnly, 0)]
    [InlineData(Caller.Guest, 0)]
    [InlineData(Caller.Share, 0)]
    public async Task ChartData_KeepsTrackerMarkersOnlyForATrackerReader(Caller caller, int markers)
    {
        var service = new Mock<IChartDataService>();
        service
            .Setup(s => s.GetDashboardChartDataAsync(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DashboardChartData { TrackerMarkers = [new TrackerMarkerDto()] });

        var controller = new ChartDataController(service.Object, NullLogger<ChartDataController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = ContextFor(caller) },
        };

        var result = await controller.GetDashboardChartData(startTime: 0, endTime: 1);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<DashboardChartData>()
            .Which.TrackerMarkers.Should().HaveCount(markers);
    }

    [Theory]
    [InlineData(Caller.MemberWithAlertsRead, 1)]
    [InlineData(Caller.MemberWithGlucoseOnly, 0)]
    public async Task WidgetSummary_KeepsTrackerStatusesOnlyForATrackerReader(Caller caller, int statuses)
    {
        var service = new Mock<IWidgetSummaryService>();
        service
            .Setup(s => s.GetSummaryAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V4SummaryResponse { Trackers = [new V4TrackerStatus()] });

        var controller = new SummaryController(service.Object, NullLogger<SummaryController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = ContextFor(caller) },
        };

        var result = await controller.GetSummary();

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<V4SummaryResponse>()
            .Which.Trackers.Should().HaveCount(statuses);
    }
}
