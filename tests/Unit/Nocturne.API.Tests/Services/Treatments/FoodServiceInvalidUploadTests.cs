using FluentAssertions;
using Moq;
using Nocturne.API.Tests.Controllers;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// A food sent with <c>isValid: false</c> is settled by the rule entries and treatments follow: stored
/// deleted under an id no food holds, deleting the live food its id names, and writing nothing under an
/// id a deleted food holds.
/// </summary>
[Trait("Category", "Unit")]
public class FoodServiceInvalidUploadTests
{
    private const string Id = "5f8d0c1e8a7b4c3d9e5f0001";

    private readonly Mock<IFoodRepository> _foods = new();
    private readonly Mock<IWriteSideEffects> _sideEffects = new();

    public FoodServiceInvalidUploadTests()
    {
        _foods
            .Setup(r => r.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Food> f, CancellationToken _) => f.ToList());
        _foods
            .Setup(r => r.StoreDeletedAsync(It.IsAny<IReadOnlyCollection<Food>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Food> f, CancellationToken _) => f.ToList());
    }

    private void Stored(bool deleted) =>
        _foods
            .Setup(r => r.GetStoredIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, bool> { [Id] = deleted });

    private static Food Invalid(string id = Id) => new() { Id = id, Name = "Apple", Carbs = 12, IsValid = false };

    [Fact]
    public async Task AnIdNoFoodHolds_IsStoredDeleted_AndNotCreated()
    {
        _foods
            .Setup(r => r.GetStoredIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, bool>());

        var written = await FoodServices.Over(_foods.Object).CreateFoodAsync([Invalid()], FoodWriteMode.Legacy);

        _foods.Verify(r => r.StoreDeletedAsync(
            It.Is<IReadOnlyCollection<Food>>(f => f.Single().Id == Id), It.IsAny<CancellationToken>()), Times.Once);
        _foods.Verify(r => r.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>()), Times.Never);
        written.Should().ContainSingle().Which.Id.Should().Be(Id);
        written.Invalidated.Should().BeEmpty();
    }

    [Fact]
    public async Task AnIdALiveFoodHolds_DeletesIt_AndAnnouncesTheDelete()
    {
        Stored(deleted: false);
        _foods.Setup(r => r.GetFoodByIdAsync(Id, It.IsAny<CancellationToken>())).ReturnsAsync(new Food { Id = Id });
        _foods.Setup(r => r.DeleteFoodAsync(Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var upload = Invalid();

        var written = await FoodServices.Over(_foods.Object, _sideEffects.Object).CreateFoodAsync([upload], FoodWriteMode.Legacy);

        written.Invalidated.Should().ContainSingle().Which.Should().BeSameAs(upload);
        _sideEffects.Verify(s => s.OnDeletedAsync(
            "food", It.Is<Food>(f => f.Id == Id), It.IsAny<WriteEffectOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        _foods.Verify(r => r.StoreDeletedAsync(It.IsAny<IReadOnlyCollection<Food>>(), It.IsAny<CancellationToken>()), Times.Never);
        _foods.Verify(r => r.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnIdADeletedFoodHolds_WritesNothing()
    {
        Stored(deleted: true);

        var written = await FoodServices.Over(_foods.Object).CreateFoodAsync([Invalid()], FoodWriteMode.Legacy);

        _foods.Verify(r => r.StoreDeletedAsync(It.IsAny<IReadOnlyCollection<Food>>(), It.IsAny<CancellationToken>()), Times.Never);
        _foods.Verify(r => r.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>()), Times.Never);
        _foods.Verify(r => r.DeleteFoodAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        written.Should().ContainSingle();
    }

    [Fact]
    public async Task AnEarlierVersionOfAnIdTakesTheValidityOfTheLast_AndTheRestOfTheBatchIsCreated()
    {
        _foods
            .Setup(r => r.GetStoredIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, bool>());
        const string otherId = "5f8d0c1e8a7b4c3d9e5f0002";
        var earlier = new Food { Id = Id, Name = "Apple", Carbs = 10 };

        await FoodServices.Over(_foods.Object).CreateFoodAsync(
            [earlier, new Food { Id = otherId, Name = "Bread", Carbs = 20 }, Invalid()], FoodWriteMode.Legacy);

        earlier.IsValid.Should().BeFalse();
        _foods.Verify(r => r.StoreDeletedAsync(
            It.Is<IReadOnlyCollection<Food>>(f => f.Single().Carbs == 12), It.IsAny<CancellationToken>()), Times.Once);
        _foods.Verify(r => r.CreateFoodAsync(
            It.Is<IEnumerable<Food>>(f => f.Single().Id == otherId), It.IsAny<CancellationToken>()), Times.Once);
    }
}
