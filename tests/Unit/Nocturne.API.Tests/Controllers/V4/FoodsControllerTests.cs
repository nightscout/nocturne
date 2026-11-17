using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

[Trait("Category", "Unit")]
public class FoodsControllerTests : IDisposable
{
    private readonly Mock<IUserFoodFavoriteService> _favoriteServiceMock;
    private readonly Mock<ITreatmentFoodService> _treatmentFoodServiceMock;
    private readonly Mock<IFoodService> _foodServiceMock;
    private readonly NocturneDbContext _dbContext;

    /// <summary>
    /// A food the controller can resolve, so a guard test that reverts the guard reaches the
    /// favorite service rather than short-circuiting on <c>NotFound</c>.
    /// </summary>
    private readonly FoodEntity _seededFood;

    /// <summary>A food with no legacy id, which v3 serves under the 24-hex prefix of its UUID.</summary>
    private readonly FoodEntity _uuidKeyedFood;

    public FoodsControllerTests()
    {
        _favoriteServiceMock = new Mock<IUserFoodFavoriteService>();
        _treatmentFoodServiceMock = new Mock<ITreatmentFoodService>();
        _foodServiceMock = new Mock<IFoodService>();

        var tenantId = Guid.NewGuid();
        _dbContext = TestDbContextFactory.CreateInMemoryContext();
        _dbContext.TenantId = tenantId;
        _seededFood = new FoodEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            OriginalId = Guid.NewGuid().ToString(),
            Name = "Test food",
        };
        _uuidKeyedFood = new FoodEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = "Uuid keyed food",
        };
        _dbContext.Foods.AddRange(_seededFood, _uuidKeyedFood);
        _dbContext.SaveChanges();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private FoodsController CreateController(AuthContext? authContext)
    {
        var controller = new FoodsController(
            new FoodRepository(_dbContext.AsFactory(), NullLogger<FoodRepository>.Instance),
            _favoriteServiceMock.Object,
            _treatmentFoodServiceMock.Object,
            _foodServiceMock.Object
        );

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
    public async Task CreateFood_WhenTheNameSanitizesToEmpty_Returns400WithNameRequired()
    {
        _foodServiceMock
            .Setup(s => s.CreateNativeFoodAsync(It.IsAny<Food>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FoodNameRequiredException());
        var controller = CreateController(null);

        var result = await controller.CreateFood(new Food { Name = "<script>alert(3)</script>" });

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeOfType<Microsoft.AspNetCore.Mvc.ProblemDetails>().Which.Detail.Should().Be("Name is required");
    }

    [Fact]
    public async Task UpdateFood_WhenTheNameSanitizesToEmpty_Returns400WithNameRequired()
    {
        _foodServiceMock
            .Setup(s => s.UpdateFoodAsync("stored", It.IsAny<Food>(), FoodWriteMode.Native, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FoodNameRequiredException());
        var controller = CreateController(null);

        var result = await controller.UpdateFood("stored", new Food { Name = "<script>alert(3)</script>" });

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeOfType<Microsoft.AspNetCore.Mvc.ProblemDetails>().Which.Detail.Should().Be("Name is required");
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
    public async Task GetFavorites_WithoutSubjectId_ReturnsEmptyListWithoutReadingAnyList()
    {
        var controller = CreateController(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.ApiKey,
            SubjectName = "admin",
            SubjectId = null,
        });

        var result = await controller.GetFavorites();

        // 200 rather than 401: remote codegen turns a query 401 into a login redirect.
        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<Food[]>()
            .Which.Should().BeEmpty();
        _favoriteServiceMock.Verify(
            x => x.GetFavoritesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task GetFavorites_WithGuestSession_ReturnsEmptyListAndNotTheOwnersList()
    {
        // A guest session authenticates with SubjectId = null and the data owner in
        // ActingAsSubjectId. It must not be served the owner's list via EffectiveSubjectId,
        // nor a list shared with every other subject-less caller.
        var ownerSubjectId = Guid.NewGuid();
        var controller = CreateController(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.Guest,
            SubjectId = null,
            ActingAsSubjectId = ownerSubjectId,
        });

        var result = await controller.GetFavorites();

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<Food[]>()
            .Which.Should().BeEmpty();
        _favoriteServiceMock.Verify(
            x => x.GetFavoritesAsync(ownerSubjectId.ToString(), It.IsAny<CancellationToken>()),
            Times.Never
        );
        _favoriteServiceMock.Verify(
            x => x.GetFavoritesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task GetFavorites_WithNoAuthContextAtAll_ReturnsEmptyList()
    {
        var controller = CreateController(null);

        var result = await controller.GetFavorites();

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<Food[]>()
            .Which.Should().BeEmpty();
        _favoriteServiceMock.Verify(
            x => x.GetFavoritesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task AddFavorite_WithoutSubjectId_ReturnsUnauthorized()
    {
        var controller = CreateController(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.ApiKey,
            SubjectId = null,
        });

        // The food resolves, so reverting the guard reaches AddFavoriteAsync and returns
        // NoContent instead of short-circuiting on NotFound.
        var result = await controller.AddFavorite(_seededFood.OriginalId!);

        result.Should().BeOfType<UnauthorizedResult>();
        _favoriteServiceMock.Verify(
            x => x.AddFavoriteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task RemoveFavorite_WithoutSubjectId_ReturnsUnauthorized()
    {
        var controller = CreateController(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.ApiKey,
            SubjectId = null,
        });

        var result = await controller.RemoveFavorite(_seededFood.OriginalId!);

        result.Should().BeOfType<UnauthorizedResult>();
        _favoriteServiceMock.Verify(
            x => x.RemoveFavoriteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task AddFavorite_WithSubjectId_ReachesTheFavoriteService()
    {
        // Pins that the seeded food resolves, so the guard tests above are refused by the
        // guard and not by an unresolvable food id.
        var subjectId = Guid.NewGuid();
        var controller = CreateController(new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = subjectId,
        });

        var result = await controller.AddFavorite(_seededFood.OriginalId!);

        result.Should().BeOfType<NoContentResult>();
        _favoriteServiceMock.Verify(
            x => x.AddFavoriteAsync(subjectId.ToString(), _seededFood.Id, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    private static AuthContext Subject(Guid subjectId) =>
        new() { IsAuthenticated = true, SubjectId = subjectId };

    public static TheoryData<string> UnresolvableIds => new() { "5f1d7a3b9c0e4a2b8d6c1e0f", "not-a-food" };

    [Fact]
    public async Task AddFavorite_ByEveryIdFormV1AndV3Accept_ResolvesTheFood()
    {
        var subjectId = Guid.NewGuid();
        var controller = CreateController(Subject(subjectId));

        foreach (var id in new[]
        {
            _uuidKeyedFood.Id.ToString(),
            MongoObjectId.Coerce(_uuidKeyedFood.Id.ToString())!,
            _seededFood.OriginalId!,
            _seededFood.Id.ToString(),
        })
        {
            (await controller.AddFavorite(id)).Should().BeOfType<NoContentResult>(id);
        }

        _favoriteServiceMock.Verify(
            x => x.AddFavoriteAsync(subjectId.ToString(), _uuidKeyedFood.Id, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        _favoriteServiceMock.Verify(
            x => x.AddFavoriteAsync(subjectId.ToString(), _seededFood.Id, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task AddFavorite_ByTheHashOfAnUppercaseLegacyId_ResolvesTheFood()
    {
        const string legacyId = "5F1D7A3B9C0E4A2B8D6C1E0F";
        _dbContext.Foods.Add(new FoodEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _seededFood.TenantId,
            OriginalId = legacyId,
            Name = "Uppercase legacy food",
        });
        await _dbContext.SaveChangesAsync();
        var subjectId = Guid.NewGuid();
        var controller = CreateController(Subject(subjectId));

        var result = await controller.AddFavorite(MongoObjectId.Coerce(legacyId)!);

        result.Should().BeOfType<NoContentResult>();
    }

    [Theory]
    [MemberData(nameof(UnresolvableIds))]
    public async Task AddFavorite_ByAnIdNoFoodHas_ReturnsNotFound(string id)
    {
        var controller = CreateController(Subject(Guid.NewGuid()));

        var result = await controller.AddFavorite(id);

        result.Should().BeOfType<NotFoundResult>();
        _favoriteServiceMock.Verify(
            x => x.AddFavoriteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RemoveFavorite_ByTheIdV3Serves_ResolvesTheFood()
    {
        var subjectId = Guid.NewGuid();
        var controller = CreateController(Subject(subjectId));

        var result = await controller.RemoveFavorite(MongoObjectId.Coerce(_uuidKeyedFood.Id.ToString())!);

        result.Should().BeOfType<NoContentResult>();
        _favoriteServiceMock.Verify(
            x => x.RemoveFavoriteAsync(subjectId.ToString(), _uuidKeyedFood.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetFoodAttributionCount_ByTheIdV3Serves_CountsTheFoodsAttributions()
    {
        _treatmentFoodServiceMock
            .Setup(x => x.CountByFoodIdAsync(_uuidKeyedFood.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        var served = MongoObjectId.Coerce(_uuidKeyedFood.Id.ToString())!;
        var controller = CreateController(null);

        var result = await controller.GetFoodAttributionCount(served);

        var count = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<FoodAttributionCount>().Subject;
        count.Count.Should().Be(3);
        count.FoodId.Should().Be(served);
    }

    [Fact]
    public async Task GetFoodAttributionCount_ByAnIdNoFoodHas_ReturnsNotFound()
    {
        var controller = CreateController(null);

        var result = await controller.GetFoodAttributionCount("5f1d7a3b9c0e4a2b8d6c1e0f");

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task DeleteFood_ByTheIdV3Serves_ClearsAttributionsAndDeletesTheFood()
    {
        var controller = CreateController(null);

        var result = await controller.DeleteFood(MongoObjectId.Coerce(_uuidKeyedFood.Id.ToString())!);

        result.Should().BeOfType<NoContentResult>();
        _treatmentFoodServiceMock.Verify(
            x => x.ClearFoodReferencesByFoodIdAsync(_uuidKeyedFood.Id, It.IsAny<CancellationToken>()),
            Times.Once);
        _foodServiceMock.Verify(
            x => x.DeleteFoodAsync(_uuidKeyedFood.Id.ToString(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteFood_WithRemoveMode_DeletesTheAttributionsOfAFoodNamedByItsLegacyId()
    {
        var controller = CreateController(null);

        var result = await controller.DeleteFood(_seededFood.OriginalId!, "remove");

        result.Should().BeOfType<NoContentResult>();
        _treatmentFoodServiceMock.Verify(
            x => x.DeleteByFoodIdAsync(_seededFood.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteFood_ByAnIdNoFoodHas_ReturnsNotFoundAndDeletesNothing()
    {
        var controller = CreateController(null);

        var result = await controller.DeleteFood("5f1d7a3b9c0e4a2b8d6c1e0f");

        result.Should().BeOfType<NotFoundResult>();
        _foodServiceMock.Verify(
            x => x.DeleteFoodAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
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
    public async Task GetRecentFoods_WithoutSubjectId_ReturnsTenantRecentsWithNothingSubtracted()
    {
        // Recents are tenant-wide; the subject only subtracts the caller's own favorites.
        // Denying this would remove a capability without adding safety, so a subject-less
        // caller still gets the list — with a null subject, so nothing is subtracted.
        var recents = new[] { new Food { Name = "Tenant recent" } };
        _favoriteServiceMock
            .Setup(x => x.GetRecentFoodsAsync(null, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(recents);

        var controller = CreateController(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.ApiKey,
            SubjectName = "admin",
            SubjectId = null,
        });

        var result = await controller.GetRecentFoods(5);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<Food[]>()
            .Which.Should().HaveCount(1);
        _favoriteServiceMock.Verify(
            x => x.GetRecentFoodsAsync(null, 5, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task GetFoods_WithoutCount_ReadsNoMoreThanTheCeiling()
    {
        var controller = CreateController(null);

        await controller.GetFoods();

        VerifyCatalogRead(count: V4ReadLimits.MaxPageSize, skip: 0);
    }

    [Fact]
    public async Task GetFoods_CountAtCeiling_ReachesServiceUnchanged()
    {
        var controller = CreateController(null);

        await controller.GetFoods(count: V4ReadLimits.MaxPageSize, skip: 0);

        VerifyCatalogRead(count: V4ReadLimits.MaxPageSize, skip: 0);
    }

    [Fact]
    public async Task GetFoods_CountAboveCeiling_IsClamped()
    {
        var controller = CreateController(null);

        await controller.GetFoods(count: V4ReadLimits.MaxPageSize + 1, skip: -1);

        VerifyCatalogRead(count: V4ReadLimits.MaxPageSize, skip: 0);
    }

    private void VerifyCatalogRead(int count, int skip) =>
        _foodServiceMock.Verify(
            x => x.GetFoodAsync(null, count, skip, It.IsAny<CancellationToken>()),
            Times.Once
        );

    [Fact]
    public async Task GetRecentFoods_LimitAtCeiling_ReachesServiceUnchanged()
    {
        var controller = CreateController(null);

        await controller.GetRecentFoods(V4ReadLimits.MaxPageSize);

        _favoriteServiceMock.Verify(
            x => x.GetRecentFoodsAsync(null, V4ReadLimits.MaxPageSize, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task GetRecentFoods_LimitAboveCeiling_IsClamped()
    {
        var controller = CreateController(null);

        await controller.GetRecentFoods(V4ReadLimits.MaxPageSize + 1);

        _favoriteServiceMock.Verify(
            x => x.GetRecentFoodsAsync(null, V4ReadLimits.MaxPageSize, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }
}
