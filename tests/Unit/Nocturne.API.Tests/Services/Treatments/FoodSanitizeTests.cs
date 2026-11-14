using FluentAssertions;
using Moq;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

public class FoodSanitizeTests
{
    private readonly Mock<IFoodRepository> _repository = new();
    private List<Food> _stored = [];

    private async Task<Food> CreateAsync(Food food, FoodWriteMode mode)
    {
        _repository
            .Setup(r => r.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Food> foods, CancellationToken _) => _stored = foods.ToList());

        var created = await Controllers.FoodServices.Over(_repository.Object).CreateFoodAsync([food], mode);
        created.Should().ContainSingle();
        return _stored.Single();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Native_MarkupOnlyFields_StoreTheirDefaults()
    {
        var stored = await CreateAsync(
            new Food
            {
                Type = "<script>alert(1)</script>",
                Category = "<script>alert(1)</script>",
                Subcategory = "<script>alert(2)</script>",
                Name = "Apple",
                Unit = "<script>alert(4)</script>",
            },
            FoodWriteMode.Native
        );

        stored.Type.Should().Be("food");
        stored.Category.Should().BeEmpty();
        stored.Subcategory.Should().BeEmpty();
        stored.Name.Should().Be("Apple");
        stored.Unit.Should().Be("g");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Native_MarkupOnlyQuickPickFields_StoreEmpty()
    {
        var stored = await CreateAsync(
            new Food
            {
                Type = "quickpick",
                Name = "Lunch",
                Foods =
                [
                    new QuickPickFood
                    {
                        Name = "<script>alert(1)</script>",
                        Unit = "<script>alert(2)</script>",
                    },
                ],
            },
            FoodWriteMode.Native
        );

        stored.Foods![0].Name.Should().BeEmpty();
        stored.Foods[0].Unit.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Native_NormalText_IsTrimmedAndKept()
    {
        var stored = await CreateAsync(
            new Food { Type = "food", Category = " Fruit ", Name = "Apple pie <script>alert(1)</script>", Unit = "ml" },
            FoodWriteMode.Native
        );

        stored.Category.Should().Be("Fruit");
        stored.Name.Should().Be("Apple pie");
        stored.Unit.Should().Be("ml");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Legacy_MarkupOnlyFields_AreStoredAsSent()
    {
        var stored = await CreateAsync(
            new Food { Type = "food", Name = "<script>alert(3)</script>", Unit = "<script>alert(4)</script>" },
            FoodWriteMode.Legacy
        );

        stored.Name.Should().Be("<script>alert(3)</script>");
        stored.Unit.Should().Be("<script>alert(4)</script>");
    }

    private async Task<Food> UpdateAsync(Food food, FoodWriteMode mode)
    {
        Food? sent = null;
        _repository
            .Setup(r => r.UpdateFoodAsync("stored", It.IsAny<Food>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Food f, CancellationToken _) => sent = f);

        var updated = await Controllers.FoodServices.Over(_repository.Object).UpdateFoodAsync("stored", food, mode);
        updated.Should().NotBeNull();
        return sent!;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Native_Update_StoresTheSanitisedValues()
    {
        var stored = await UpdateAsync(
            new Food
            {
                Type = "<script>alert(1)</script>",
                Category = " Fruit <script>alert(2)</script>",
                Name = "Apple pie <script>alert(3)</script>",
                Unit = "<script>alert(4)</script>",
            },
            FoodWriteMode.Native
        );

        stored.Type.Should().Be("food");
        stored.Category.Should().Be("Fruit");
        stored.Name.Should().Be("Apple pie");
        stored.Unit.Should().Be("g");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Legacy_Update_StoresTextAsSent()
    {
        var stored = await UpdateAsync(
            new Food { Type = "food", Name = "<script>alert(3)</script>", Unit = "<script>alert(4)</script>" },
            FoodWriteMode.Legacy
        );

        stored.Name.Should().Be("<script>alert(3)</script>");
        stored.Unit.Should().Be("<script>alert(4)</script>");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Native_Batch_SkipsAFoodWhoseNameIsMarkupOnlyAndCreatesTheRest()
    {
        _repository
            .Setup(r => r.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Food> foods, CancellationToken _) => _stored = foods.ToList());

        var created = await Controllers.FoodServices.Over(_repository.Object).CreateFoodAsync(
            [new Food { Name = "Apple" }, new Food { Name = "<script>alert(3)</script>" }, new Food { Name = "Pear" }],
            FoodWriteMode.Native
        );

        created.Select(f => f.Name).Should().Equal("Apple", "Pear");
        _stored.Select(f => f.Name).Should().Equal("Apple", "Pear");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Native_Batch_OfOnlyRefusedFoods_CreatesNothingAndDoesNotThrow()
    {
        _repository
            .Setup(r => r.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Food> foods, CancellationToken _) => _stored = foods.ToList());

        var created = await Controllers.FoodServices.Over(_repository.Object).CreateFoodAsync(
            [new Food { Name = "<script>alert(3)</script>" }],
            FoodWriteMode.Native
        );

        created.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateNative_WithMarkupOnlyName_IsRefusedBeforeAnyWrite()
    {
        var service = Controllers.FoodServices.Over(_repository.Object);

        var create = () => service.CreateNativeFoodAsync(new Food { Name = "<script>alert(3)</script>" });

        await create.Should().ThrowAsync<FoodNameRequiredException>();
        _repository.Verify(r => r.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateNative_StoresTheSanitisedFood()
    {
        _repository
            .Setup(r => r.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Food> foods, CancellationToken _) => _stored = foods.ToList());

        var created = await Controllers.FoodServices.Over(_repository.Object)
            .CreateNativeFoodAsync(new Food { Name = " Apple <script>alert(1)</script> " });

        created.Name.Should().Be("Apple");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Native_Update_WithMarkupOnlyName_IsRefusedBeforeAnyWrite()
    {
        var service = Controllers.FoodServices.Over(_repository.Object);

        var update = () => service.UpdateFoodAsync("stored", new Food { Name = "<script>alert(3)</script>" }, FoodWriteMode.Native);

        await update.Should().ThrowAsync<FoodNameRequiredException>();
        _repository.Verify(
            r => r.UpdateFoodAsync(It.IsAny<string>(), It.IsAny<Food>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }
}
