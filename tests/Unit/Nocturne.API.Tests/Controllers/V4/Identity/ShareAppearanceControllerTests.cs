using System.Linq;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// The share appearance endpoint is reachable without credentials, so two things have to hold:
/// it answers only on a share host, and what it answers with is presentation, never identity.
/// </summary>
public sealed class ShareAppearanceControllerTests
{
    private readonly Mock<IShareLinkService> _service = new();

    private ShareAppearanceController BuildController(bool onShareHost)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(t => t.TenantId).Returns(Guid.NewGuid());

        var httpContext = new DefaultHttpContext();
        if (onShareHost)
            httpContext.SetShareAccess();

        return new ShareAppearanceController(_service.Object, tenantAccessor.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    [Fact]
    public async Task GetShareAppearance_off_a_share_host_is_not_found_and_reads_nothing()
    {
        var controller = BuildController(onShareHost: false);

        var result = await controller.GetShareAppearance(CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        _service.Verify(
            s => s.GetSharedAppearanceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetShareAppearance_on_a_share_host_returns_the_owners_presentation_settings()
    {
        _service
            .Setup(s => s.GetSharedAppearanceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserDisplayPreferences { GlucoseUnits = "mmol", TimeFormat = "24" });
        var controller = BuildController(onShareHost: true);

        var result = await controller.GetShareAppearance(CancellationToken.None);

        var value = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<UserDisplayPreferences>().Subject;
        value.GlucoseUnits.Should().Be("mmol");
        value.TimeFormat.Should().Be("24");
    }

    /// <summary>
    /// The disclosure boundary, pinned by shape rather than by one response: an anonymous viewer
    /// reads how the data is drawn and nothing about who owns it. Adding a field to the
    /// presentation projection fails here until it is weighed.
    /// </summary>
    [Fact]
    public void The_response_carries_presentation_fields_only()
    {
        var everythingSet = new UserDisplayPreferences
        {
            GlucoseUnits = "mmol",
            TimeFormat = "24",
            RegionFormat = "en-GB",
            ColorTheme = "trio",
            NightModeSchedule = true,
            Prediction = new PredictionPreferences { Enabled = true },
            Chart = new ChartPreferences { ShowPoints = true },
            DashboardTopWidgets = [WidgetId.Tdd],
        };

        var disclosed = everythingSet.ToPresentationOnly();

        SetPropertyNames(disclosed).Should().BeEquivalentTo(
            "GlucoseUnits", "TimeFormat", "RegionFormat", "ColorTheme", "Prediction", "Chart");

        // Both are carried whole rather than field by field, so the projection cannot withhold a
        // field added inside them: everything these two types declare is disclosed. Pinning the
        // declarations is what makes adding one a decision rather than a default.
        PropertyNames(typeof(PredictionPreferences)).Should().BeEquivalentTo(
            "Enabled", "Minutes", "DisplayMode");
        PropertyNames(typeof(ChartPreferences)).Should().BeEquivalentTo(
            "LineColorMode", "LineColor", "PointColorMode", "PointColor", "ShowPoints",
            "AreaMode", "AreaOpacity", "AlwaysShowPatterns", "Lookback");
    }

    private static IEnumerable<string> PropertyNames(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);

    /// <summary>
    /// The names of the properties carrying a value. Every display preference is nullable, so
    /// applied to a projection of an all-set instance this is exactly what the projection let
    /// through.
    /// </summary>
    private static IEnumerable<string> SetPropertyNames(UserDisplayPreferences preferences) =>
        typeof(UserDisplayPreferences)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetValue(preferences) is not null)
            .Select(p => p.Name);
}
