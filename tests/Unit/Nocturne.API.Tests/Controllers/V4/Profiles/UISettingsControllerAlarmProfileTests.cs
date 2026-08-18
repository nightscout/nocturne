using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.API.Services.Profiles;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Profiles;

/// <summary>
/// Unit coverage for the per-profile alarm routes on <see cref="UISettingsController"/>. Each write
/// is asserted through the matching read: the profile list these routes edit is the same
/// <see cref="UserAlarmConfiguration"/> blob <c>GET notifications/alarms</c> returns.
/// </summary>
[Trait("Category", "Unit")]
public class UISettingsControllerAlarmProfileTests
{
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task AddOrUpdateAlarmProfile_isVisibleToGetAlarmConfiguration()
    {
        var controller = NewController();

        await controller.AddOrUpdateAlarmProfile(new AlarmProfileConfiguration
        {
            Id = "night-high",
            Name = "Nighttime High",
            AlarmType = AlarmTriggerType.High,
            Threshold = 220,
        });

        var profiles = AlarmConfigOf(await controller.GetAlarmConfiguration()).Profiles;
        profiles.Should().ContainSingle(p => p.Id == "night-high")
            .Which.Threshold.Should().Be(220);
    }

    [Fact]
    public async Task AddOrUpdateAlarmProfile_replacesAnExistingProfileInPlace()
    {
        var controller = NewController();

        await controller.AddOrUpdateAlarmProfile(new AlarmProfileConfiguration
        {
            Id = "night-high",
            Name = "Nighttime High",
            Threshold = 220,
        });
        await controller.AddOrUpdateAlarmProfile(new AlarmProfileConfiguration
        {
            Id = "night-high",
            Name = "Nighttime High",
            Threshold = 190,
        });

        var profiles = AlarmConfigOf(await controller.GetAlarmConfiguration()).Profiles;
        profiles.Should().ContainSingle().Which.Threshold.Should().Be(190);
    }

    [Fact]
    public async Task AddOrUpdateAlarmProfile_assignsAnId_soBlankIdProfilesDoNotCollide()
    {
        var controller = NewController();

        await controller.AddOrUpdateAlarmProfile(new AlarmProfileConfiguration
        {
            Id = string.Empty,
            Name = "First",
        });
        await controller.AddOrUpdateAlarmProfile(new AlarmProfileConfiguration
        {
            Id = string.Empty,
            Name = "Second",
        });

        var profiles = AlarmConfigOf(await controller.GetAlarmConfiguration()).Profiles;
        profiles.Should().HaveCount(2);
        profiles.Select(p => p.Id).Should().OnlyHaveUniqueItems().And.NotContain(string.Empty);
    }

    [Fact]
    public async Task DeleteAlarmProfile_removesItFromGetAlarmConfiguration()
    {
        var controller = NewController();

        await controller.AddOrUpdateAlarmProfile(new AlarmProfileConfiguration { Id = "keep" });
        await controller.AddOrUpdateAlarmProfile(new AlarmProfileConfiguration { Id = "drop" });

        await controller.DeleteAlarmProfile("drop");

        AlarmConfigOf(await controller.GetAlarmConfiguration()).Profiles
            .Select(p => p.Id).Should().Equal("keep");
    }

    [Fact]
    public async Task DeleteAlarmProfile_reports404_forAnUnknownProfile()
    {
        var controller = NewController();

        var result = await controller.DeleteAlarmProfile("never-existed");

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    // ----- helpers -----

    private static UserAlarmConfiguration AlarmConfigOf(
        ActionResult<UserAlarmConfiguration> result)
    {
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeAssignableTo<UserAlarmConfiguration>().Subject;
    }

    private static UISettingsController NewController()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var dbContext = new NocturneDbContext(options) { TenantId = TenantId };
        var configuration = new ConfigurationBuilder().Build();

        var services = new ServiceCollection();
        services.AddControllers();

        return new UISettingsController(
            NullLogger<UISettingsController>.Instance,
            configuration,
            Mock.Of<IHttpClientFactory>(),
            new UISettingsService(
                dbContext,
                NullLogger<UISettingsService>.Instance,
                configuration))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = services.BuildServiceProvider(),
                },
            },
        };
    }
}
