using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Profiles;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using static Nocturne.API.Tests.Controllers.V4.Profiles.UISettingsControllerHarness;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

/// <summary>
/// <see cref="UISettingsController"/> serves every member the presentation sections and withholds a
/// section whose <see cref="UISettingsSection.ReadScope"/> the caller does not hold.
/// </summary>
[Trait("Category", "Unit")]
public class UISettingsControllerReadScopeTests
{
    [Fact]
    public async Task GetUISettings_withholdsEverySectionAViewerCannotRead()
    {
        var controller = Controller(NewDatabase(), Scope.GlucoseRead, Scope.ReportsRead);

        var settings = OkValue<UISettingsConfiguration>((await controller.GetUISettings()).Result);

        settings.Devices.Should().BeNull();
        settings.Notifications.Should().BeNull();
        settings.Services.Should().BeNull();
        settings.Features.Should().NotBeNull();
        settings.Algorithm.Should().NotBeNull();
        settings.DataQuality.Should().NotBeNull();
    }

    [Fact]
    public async Task GetUISettings_servesASectionWhoseScopeTheCallerHolds()
    {
        var controller = Controller(NewDatabase(), Scope.AlertsRead, Scope.DevicesRead);

        var settings = OkValue<UISettingsConfiguration>((await controller.GetUISettings()).Result);

        settings.Notifications.Should().NotBeNull();
        settings.Devices.Should().NotBeNull();
        settings.Services.Should().BeNull();
    }

    [Theory]
    [InlineData("notifications", Scope.AlertsRead)]
    [InlineData("devices", Scope.DevicesRead)]
    [InlineData("services", Scope.TenantSettings)]
    public async Task GetSectionSettings_refusesASectionTheCallerCannotRead(string section, string scope)
    {
        var controller = Controller(NewDatabase(), Scope.GlucoseRead);

        var result = (await controller.GetSectionSettings(section)).Result;

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ((ProblemDetails)((ObjectResult)result!).Value!).Detail.Should().Contain(scope);
    }

    /// <summary>
    /// A guest link holds alerts.read by default, and the alarm configuration carries emergency
    /// contacts and push-delivery keys.
    /// </summary>
    [Fact]
    public async Task AGuestHoldingAlertsRead_IsRefusedTheNotificationsSection()
    {
        var controller = Controller(NewDatabase(), Scope.AlertsRead, Scope.GlucoseRead);
        controller.HttpContext.SetAuthContext(new AuthContext { IsAuthenticated = true, AuthType = AuthType.Guest });

        var settings = OkValue<UISettingsConfiguration>((await controller.GetUISettings()).Result);
        var section = (await controller.GetSectionSettings("notifications")).Result;

        settings.Notifications.Should().BeNull();
        settings.Features.Should().NotBeNull();
        section.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task GetSectionSettings_servesAPresentationSectionToAnyMember()
    {
        var controller = Controller(NewDatabase(), Scope.GlucoseRead);

        (await controller.GetSectionSettings("features")).Result.Should().BeOfType<OkObjectResult>();
    }

    /// <summary>
    /// A section save is a read-modify-write over the narrowed body, so a withheld section comes
    /// back null and must leave the stored value alone.
    /// </summary>
    [Fact]
    public async Task SavingANarrowedBody_leavesTheWithheldSectionsStored()
    {
        var database = NewDatabase();
        var owner = Controller(database, Scope.FullAccess);
        await owner.SaveUISettings(new UISettingsConfiguration
        {
            Devices = new DeviceSettings { ShowRawData = true },
        });

        var narrowed = OkValue<UISettingsConfiguration>(
            (await Controller(database, Scope.AlertsReadWrite).GetUISettings()).Result);
        await Controller(database, Scope.AlertsReadWrite).SaveUISettings(narrowed);

        var stored = OkValue<UISettingsConfiguration>((await owner.GetUISettings()).Result);
        stored.Devices!.ShowRawData.Should().BeTrue();
    }

    private static UISettingsController Controller(
        NocturneDbContext database, params string[] scopes) =>
        NewController(
            new UISettingsService(database.AsFactory(), NullLogger<UISettingsService>.Instance),
            DemoMode(false),
            Mock.Of<IHttpClientFactory>(),
            scopes);
}
