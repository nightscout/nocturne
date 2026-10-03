using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V1;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// v1 PUT /profile, DELETE /profile/{id} and DELETE /profile?keep=N, as Nightscout routes them.
/// </summary>
[Trait("Category", "Unit")]
public class ProfileWriteRoutesTests
{
    private const string StoredId = "5f8d0c1e8a7b4c3d9e5f0001";

    [Fact]
    public async Task UpdateProfile_SavesUnderTheBodyId_AndAnswersTheSavedDocument()
    {
        var saved = new Profile { Id = StoredId, DefaultProfile = "Saved" };
        var service = new Mock<IProfileWriteService>();
        service
            .Setup(s => s.UpdateProfileAsync(StoredId, It.IsAny<Profile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(saved);

        var result = await NewController(service.Object, "").UpdateProfile(Document(StoredId, "Edited"));

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(saved);
        service.Verify(
            s => s.UpdateProfileAsync(
                StoredId,
                It.Is<Profile>(p => p.DefaultProfile == "Edited"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task UpdateProfile_WithoutAnId_IsRefused(string? id)
    {
        var service = new Mock<IProfileWriteService>(MockBehavior.Strict);

        var result = await NewController(service.Object, "").UpdateProfile(Document(id, "Edited"));

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UpdateProfile_WithoutAStore_IsRefused()
    {
        var service = new Mock<IProfileWriteService>(MockBehavior.Strict);

        var result = await NewController(service.Object, "")
            .UpdateProfile(new Profile { Id = StoredId, DefaultProfile = "Edited" });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UpdateProfile_TheServiceRefused_AnswersConflict()
    {
        var service = new Mock<IProfileWriteService>();
        service
            .Setup(s => s.UpdateProfileAsync(StoredId, It.IsAny<Profile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Profile?)null);

        var result = await NewController(service.Object, "").UpdateProfile(Document(StoredId, "Edited"));

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task DeleteProfile_AnswersOkWithTheDeleteStatus_FoundOrNot(bool deleted, long count)
    {
        var service = new Mock<IProfileWriteService>();
        service.Setup(s => s.DeleteProfileAsync(StoredId, It.IsAny<CancellationToken>())).ReturnsAsync(deleted);

        var result = await NewController(service.Object, "").DeleteProfile(StoredId);

        AssertDeleteStatus(result, count);
    }

    [Theory]
    [InlineData("?keep=10", 10)]
    [InlineData("?keep=10000", 10000)]
    [InlineData("", 100)]
    public async Task PruneProfiles_KeepsTheRequestedCount_AndCarriesDeletedCount(string query, int keep)
    {
        var service = new Mock<IProfileWriteService>();
        service.Setup(s => s.PruneProfilesAsync(keep, It.IsAny<CancellationToken>())).ReturnsAsync(4);

        var result = await NewController(service.Object, query).PruneProfiles();

        AssertDeleteStatus(result, 4);
    }

    [Theory]
    [InlineData("?keep=")]
    [InlineData("?keep=abc")]
    [InlineData("?keep=-5")]
    [InlineData("?keep=0")]
    [InlineData("?keep=9")]
    [InlineData("?keep=10001")]
    [InlineData("?keep=12.5")]
    public async Task PruneProfiles_KeepOutsideNightscoutsBounds_IsRefused(string query)
    {
        var service = new Mock<IProfileWriteService>(MockBehavior.Strict);

        var result = await NewController(service.Object, query).PruneProfiles();

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(nameof(ProfileController.UpdateProfile), Scope.TherapyRead)]
    [InlineData(nameof(ProfileController.DeleteProfile), Scope.TherapyRead)]
    [InlineData(nameof(ProfileController.PruneProfiles), Scope.TherapyRead)]
    [InlineData(nameof(ProfileController.PruneProfiles), Scope.TherapyReadWrite)]
    public void WriteRoute_RefusesAGrantBelowItsScope(string action, string granted)
    {
        EvaluateScope(action, granted).Should().BeOfType<ForbidResult>();
    }

    [Theory]
    [InlineData(nameof(ProfileController.UpdateProfile), Scope.TherapyReadWrite)]
    [InlineData(nameof(ProfileController.DeleteProfile), Scope.TherapyReadWrite)]
    [InlineData(nameof(ProfileController.PruneProfiles), Scope.FullAccess)]
    public void WriteRoute_AdmitsItsScope(string action, string granted)
    {
        EvaluateScope(action, granted).Should().BeNull();
    }

    private static Profile Document(string? id, string defaultProfile) => new()
    {
        Id = id,
        DefaultProfile = defaultProfile,
        Store = { [defaultProfile] = new ProfileData() },
    };

    private static IActionResult? EvaluateScope(string action, string granted)
    {
        var attribute = typeof(ProfileController).GetMethod(action)!.GetCustomAttribute<RequireScopeAttribute>()!;
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true };
        httpContext.Items["GrantedScopes"] = Scope.Normalize([granted]);
        var filterContext = new AuthorizationFilterContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>());

        attribute.OnAuthorization(filterContext);
        return filterContext.Result;
    }

    private static void AssertDeleteStatus(ActionResult result, long count)
    {
        var body = JsonSerializer.SerializeToElement(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        body.GetProperty("acknowledged").GetBoolean().Should().BeTrue();
        body.GetProperty("deletedCount").GetInt64().Should().Be(count);
        body.GetProperty("n").GetInt64().Should().Be(count);
    }

    private static ProfileController NewController(IProfileWriteService service, string queryString)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(queryString);
        return new ProfileController(
            Mock.Of<IProfileProjectionService>(), service, NullLogger<ProfileController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }
}
