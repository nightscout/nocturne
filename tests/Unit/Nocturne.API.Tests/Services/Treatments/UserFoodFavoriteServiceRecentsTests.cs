using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// Recents are tenant-wide; the subject only determines which foods are subtracted as the
/// caller's favorites. A subject-less caller therefore still gets the list, with nothing
/// subtracted and no per-subject lookup performed.
/// </summary>
[Trait("Category", "Unit")]
public class UserFoodFavoriteServiceRecentsTests
{
    private readonly Mock<IUserFoodFavoriteRepository> _favoriteRepositoryMock = new();
    private readonly Mock<ITreatmentFoodRepository> _treatmentFoodRepositoryMock = new();

    private UserFoodFavoriteService CreateService() =>
        new(
            _favoriteRepositoryMock.Object,
            _treatmentFoodRepositoryMock.Object,
            Mock.Of<ILogger<UserFoodFavoriteService>>()
        );

    private static Food FoodWith(string id, string name) => new() { Id = id, Name = name };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task GetRecentFoods_WithNoSubject_SubtractsNothingAndSkipsTheFavoritesLookup(
        string? userId)
    {
        var recent = FoodWith(Guid.NewGuid().ToString(), "Recent");
        _treatmentFoodRepositoryMock
            .Setup(x => x.GetRecentFoodsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([recent]);
        // Answer the favorites lookup so that skipping it is what this test pins, rather than
        // the lookup throwing on an unconfigured mock.
        _favoriteRepositoryMock
            .Setup(x => x.GetFavoriteFoodsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await CreateService().GetRecentFoodsAsync(userId, 20);

        result.Should().ContainSingle().Which.Id.Should().Be(recent.Id);
        _favoriteRepositoryMock.Verify(
            x => x.GetFavoriteFoodsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task GetRecentFoods_WithSubject_SubtractsThatSubjectsFavorites()
    {
        var favoriteId = Guid.NewGuid().ToString();
        var keptId = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid().ToString();

        _favoriteRepositoryMock
            .Setup(x => x.GetFavoriteFoodsAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([FoodWith(favoriteId, "Favorite")]);
        _treatmentFoodRepositoryMock
            .Setup(x => x.GetRecentFoodsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([FoodWith(favoriteId, "Favorite"), FoodWith(keptId, "Recent")]);

        var result = await CreateService().GetRecentFoodsAsync(userId, 20);

        result.Should().ContainSingle().Which.Id.Should().Be(keptId);
    }
}
