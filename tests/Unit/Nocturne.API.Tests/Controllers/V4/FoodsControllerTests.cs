using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

[Trait("Category", "Unit")]
public class FoodsControllerTests
{
    private readonly Mock<IUserFoodFavoriteService> _favoriteServiceMock;

    public FoodsControllerTests()
    {
        _favoriteServiceMock = new Mock<IUserFoodFavoriteService>();
    }

    private FoodsController CreateController(AuthContext? authContext)
    {
        using var dbContext = TestDbContextFactory.CreateInMemoryContext();
        var controller = new FoodsController(dbContext, _favoriteServiceMock.Object);

        var httpContext = new DefaultHttpContext();
        if (authContext != null)
        {
            httpContext.Items["AuthContext"] = authContext;
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };

        return controller;
    }

    [Fact]
    public async Task GetFavorites_WithSubjectId_ReturnsOk()
    {
        var subjectId = Guid.NewGuid();
        _favoriteServiceMock
            .Setup(x => x.GetFavoritesAsync(subjectId.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Food>());

        var controller = CreateController(new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = subjectId,
        });

        var result = await controller.GetFavorites();

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetFavorites_WithoutSubjectId_ReturnsOkWithDefaultUser()
    {
        // This is the regression test: API secret auth sets IsAuthenticated but no SubjectId.
        // Previously this returned 403 Forbid.
        const string defaultUserId = "00000000-0000-0000-0000-000000000001";
        _favoriteServiceMock
            .Setup(x => x.GetFavoritesAsync(defaultUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Food>());

        var controller = CreateController(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.ApiSecret,
            SubjectName = "admin",
            SubjectId = null,
        });

        var result = await controller.GetFavorites();

        result.Result.Should().BeOfType<OkObjectResult>();
        _favoriteServiceMock.Verify(
            x => x.GetFavoritesAsync(defaultUserId, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task GetRecentFoods_WithSubjectId_ReturnsOk()
    {
        var subjectId = Guid.NewGuid();
        _favoriteServiceMock
            .Setup(x => x.GetRecentFoodsAsync(subjectId.ToString(), 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Food>());

        var controller = CreateController(new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = subjectId,
        });

        var result = await controller.GetRecentFoods();

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetRecentFoods_WithoutSubjectId_ReturnsOkWithDefaultUser()
    {
        const string defaultUserId = "00000000-0000-0000-0000-000000000001";
        _favoriteServiceMock
            .Setup(x => x.GetRecentFoodsAsync(defaultUserId, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Food>());

        var controller = CreateController(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.ApiSecret,
            SubjectName = "admin",
            SubjectId = null,
        });

        var result = await controller.GetRecentFoods(5);

        result.Result.Should().BeOfType<OkObjectResult>();
        _favoriteServiceMock.Verify(
            x => x.GetRecentFoodsAsync(defaultUserId, 5, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }
}
