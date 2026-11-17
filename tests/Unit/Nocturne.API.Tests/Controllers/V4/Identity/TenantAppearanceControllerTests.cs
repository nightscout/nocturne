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
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// The tenant appearance endpoint is reachable without credentials, so two things have to hold:
/// it answers only a share or a guest, and what it answers with is presentation, never identity.
/// </summary>
public sealed class TenantAppearanceControllerTests
{
    // The narrow interface, not IShareLinkService: mocking the wide one would still compile if
    // the controller took a dependency that can hand out the share token, which is the thing this
    // seam exists to prevent.
    private readonly Mock<ITenantAppearanceReader> _service = new();

    public TenantAppearanceControllerTests()
    {
        _service
            .Setup(s => s.GetTenantAppearanceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserDisplayPreferences { GlucoseUnits = "mmol", TimeFormat = "24" });
    }

    private TenantAppearanceController BuildController(bool onShareHost = false, AuthContext? auth = null)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(t => t.TenantId).Returns(Guid.NewGuid());

        var httpContext = new DefaultHttpContext();
        if (onShareHost)
            httpContext.SetShareAccess();
        if (auth is not null)
            httpContext.SetAuthContext(auth);

        return new TenantAppearanceController(_service.Object, tenantAccessor.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private async Task ShouldAnswerWithTheOwnersPresentation(TenantAppearanceController controller)
    {
        var result = await controller.GetTenantAppearance(CancellationToken.None);

        var value = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<UserDisplayPreferences>().Subject;
        value.GlucoseUnits.Should().Be("mmol");
        value.TimeFormat.Should().Be("24");
    }

    private async Task ShouldBeNotFoundAndReadNothing(TenantAppearanceController controller)
    {
        var result = await controller.GetTenantAppearance(CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        _service.Verify(
            s => s.GetTenantAppearanceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public Task A_share_viewer_gets_the_owners_presentation_settings() =>
        ShouldAnswerWithTheOwnersPresentation(BuildController(onShareHost: true));

    [Fact]
    public Task A_guest_gets_the_owners_presentation_settings() =>
        ShouldAnswerWithTheOwnersPresentation(BuildController(auth: new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.Guest,
            ActingAsSubjectId = Guid.NewGuid(),
            Scopes = ["glucose.read"],
        }));

    [Fact]
    public Task An_anonymous_caller_off_a_share_host_is_not_found() =>
        ShouldBeNotFoundAndReadNothing(BuildController(auth: AuthContext.Unauthenticated()));

    [Fact]
    public Task A_member_is_not_found_because_they_read_their_own_settings() =>
        ShouldBeNotFoundAndReadNothing(BuildController(auth: new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = Guid.NewGuid(),
        }));

    [Fact]
    public Task A_guest_session_the_auth_middleware_rejected_is_not_found() =>
        ShouldBeNotFoundAndReadNothing(BuildController(auth: new AuthContext
        {
            IsAuthenticated = false,
            AuthType = AuthType.Guest,
        }));

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
            A1cName = "A1c",
            A1cUnits = "mmol/mol",
            CarbRatioFormat = "U/10g",
            TimeFormat = "24",
            RegionFormat = "en-GB",
            EnergyUnit = "kJ",
            ColorTheme = "trio",
            GlucoseRangePalette = "clarity",
            NightModeSchedule = true,
            CalendarShowTirPercent = true,
            Prediction = new PredictionPreferences { Enabled = true },
            Chart = new ChartPreferences { ShowPoints = true },
            YearOverviewColors = new YearOverviewColorPreferences { TddBand = [10, 70] },
            DashboardTopWidgets = [WidgetId.Tdd],
            DashboardRail = new DashboardRailPreference { Hidden = [RailItemId.Trackers] },
            HiddenLegacyFallbacks = [Nocturne.Core.Models.LegacyConsumableKind.Site],
        };

        var disclosed = everythingSet.ToPresentationOnly();

        SetPropertyNames(disclosed).Should().BeEquivalentTo(
            "GlucoseUnits", "A1cName", "A1cUnits", "CarbRatioFormat", "TimeFormat", "RegionFormat", "EnergyUnit", "EffectiveEnergyUnit",
            "ColorTheme", "GlucoseRangePalette", "CalendarShowTirPercent", "Prediction", "Chart", "YearOverviewColors");

        // Both are carried whole rather than field by field, so the projection cannot withhold a
        // field added inside them: everything these two types declare is disclosed. Pinning the
        // declarations is what makes adding one a decision rather than a default.
        PropertyNames(typeof(YearOverviewColorPreferences)).Should().BeEquivalentTo(
            "OutOfBandTransparency",
            "AvgGlucoseBand", "AvgGlucoseBoundaries", "AvgGlucoseLowColor", "AvgGlucoseHighColor",
            "TirBand", "TirBoundaries", "TirLowColor", "TirHighColor",
            "BolusBand", "BolusBoundaries", "BolusLowColor", "BolusHighColor",
            "BasalBand", "BasalBoundaries", "BasalLowColor", "BasalHighColor",
            "TddBand", "TddBoundaries", "TddLowColor", "TddHighColor",
            "CarbsBand", "CarbsBoundaries", "CarbsLowColor", "CarbsHighColor");
        PropertyNames(typeof(YearOverviewColorBoundary)).Should().BeEquivalentTo("Value", "Position", "Color");
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
