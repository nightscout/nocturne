using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// Unit coverage for <see cref="UserPreferencesController"/> — the per-user display-preferences
/// store that fixes cross-device drift (nocturne#520). Focuses on the request wiring: partial
/// merge over the stored blob, validation, defaults when unset, and the unauthenticated gate.
/// </summary>
[Trait("Category", "Unit")]
public class UserPreferencesControllerTests
{
    private static readonly Guid SubjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task GetPreferences_returnsEmptyDefaults_whenNothingStored()
    {
        var (controller, _) = NewController();

        var result = await controller.GetPreferences();

        var value = OkValue(result);
        value.Preferences.Should().NotBeNull();
        value.Preferences.GlucoseUnits.Should().BeNull();
    }

    [Fact]
    public async Task UpdatePreferences_persists_andGetReturnsIt()
    {
        var (controller, options) = NewController();

        await controller.UpdatePreferences(new UpdateUserPreferencesRequest
        {
            Preferences = new UserDisplayPreferences { GlucoseUnits = "mmol" },
        });

        var value = OkValue(await controller.GetPreferences());
        value.Preferences.GlucoseUnits.Should().Be("mmol");

        // And it is actually written to the subject row as a jsonb blob.
        await using var db = new NocturneDbContext(options);
        (await db.Subjects.FirstAsync(s => s.Id == SubjectId)).Preferences.Should().Contain("mmol");
    }

    [Fact]
    public async Task UpdatePreferences_mergesPartial_withoutClobberingUnsetFields()
    {
        var (controller, _) = NewController();

        await controller.UpdatePreferences(new UpdateUserPreferencesRequest
        {
            Preferences = new UserDisplayPreferences { GlucoseUnits = "mmol", TimeFormat = "24" },
        });

        // Second PATCH only touches the theme — units and time format must survive.
        await controller.UpdatePreferences(new UpdateUserPreferencesRequest
        {
            Preferences = new UserDisplayPreferences { ColorTheme = "trio" },
        });

        var value = OkValue(await controller.GetPreferences());
        value.Preferences.GlucoseUnits.Should().Be("mmol");
        value.Preferences.TimeFormat.Should().Be("24");
        value.Preferences.ColorTheme.Should().Be("trio");
    }

    [Fact]
    public async Task UpdatePreferences_rejectsInvalidUnits()
    {
        var (controller, _) = NewController();

        var result = await controller.UpdatePreferences(new UpdateUserPreferencesRequest
        {
            Preferences = new UserDisplayPreferences { GlucoseUnits = "mgdl" },
        });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetPreferences_unauthenticated_returns401()
    {
        var (controller, _) = NewController(authenticated: false);

        var result = await controller.GetPreferences();

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    // ----- helpers -----

    private static (UserPreferencesController Controller, DbContextOptions<NocturneDbContext> Options) NewController(
        bool authenticated = true)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using (var seed = new NocturneDbContext(options))
        {
            seed.Subjects.Add(new SubjectEntity { Id = SubjectId, Name = "Test" });
            seed.SaveChanges();
        }

        var dbContext = new NocturneDbContext(options);
        var controller = new UserPreferencesController(dbContext, NullLogger<UserPreferencesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = HttpContextWith(authenticated) },
        };
        return (controller, options);
    }

    private static DefaultHttpContext HttpContextWith(bool authenticated)
    {
        var context = new DefaultHttpContext();
        if (authenticated)
        {
            context.Items["AuthContext"] = new AuthContext
            {
                IsAuthenticated = true,
                SubjectId = SubjectId,
            };
        }
        return context;
    }

    private static UserPreferencesResponse OkValue(ActionResult<UserPreferencesResponse> result)
    {
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeAssignableTo<UserPreferencesResponse>().Subject;
    }
}
