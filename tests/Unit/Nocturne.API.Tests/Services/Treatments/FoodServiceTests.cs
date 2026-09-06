using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Models;
using Nocturne.Core.Contracts.Repositories;
using Xunit;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// Unit tests for FoodService domain service with WebSocket broadcasting
/// </summary>
public class FoodServiceTests
{
    private readonly Mock<IFoodRepository> _mockFoodRepository;
    private readonly Mock<IDocumentProcessingService> _mockDocumentProcessingService;
    private readonly Mock<IWriteSideEffects> _mockSideEffects;
    private readonly Mock<ILogger<FoodService>> _mockLogger;
    private readonly FoodService _foodService;

    public FoodServiceTests()
    {
        _mockFoodRepository = new Mock<IFoodRepository>();
        _mockDocumentProcessingService = new Mock<IDocumentProcessingService>();
        _mockSideEffects = new Mock<IWriteSideEffects>();
        _mockLogger = new Mock<ILogger<FoodService>>();

        _foodService = new FoodService(
            _mockFoodRepository.Object,
            _mockDocumentProcessingService.Object,
            _mockSideEffects.Object,
            Mock.Of<IDataEventSink<Food>>(),
            _mockLogger.Object
        );
    }

    #region Constructor Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public void Constructor_WithValidDependencies_ShouldCreateInstance()
    {
        // Arrange & Act
        var service = new FoodService(
            _mockFoodRepository.Object,
            _mockDocumentProcessingService.Object,
            _mockSideEffects.Object,
            Mock.Of<IDataEventSink<Food>>(),
            _mockLogger.Object
        );

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public void Constructor_WithNullMongoDbService_ShouldThrowArgumentNullException()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new FoodService(
                null!,
                _mockDocumentProcessingService.Object,
                _mockSideEffects.Object,
                Mock.Of<IDataEventSink<Food>>(),
                _mockLogger.Object
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public void Constructor_WithNullDocumentProcessingService_ShouldThrowArgumentNullException()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new FoodService(
                _mockFoodRepository.Object,
                null!,
                _mockSideEffects.Object,
                Mock.Of<IDataEventSink<Food>>(),
                _mockLogger.Object
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public void Constructor_WithNullSignalRBroadcastService_ShouldThrowArgumentNullException()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new FoodService(
                _mockFoodRepository.Object,
                _mockDocumentProcessingService.Object,
                null!,
                Mock.Of<IDataEventSink<Food>>(),
                _mockLogger.Object
            )
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public void Constructor_WithNullLogger_ShouldThrowArgumentNullException()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new FoodService(
                _mockFoodRepository.Object,
                _mockDocumentProcessingService.Object,
                _mockSideEffects.Object,
                Mock.Of<IDataEventSink<Food>>(),
                null!
            )
        );
    }

    #endregion

    #region GetFoodAsync Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task GetFoodAsync_WithoutParameters_ReturnsTheDefaultPage()
    {
        // Arrange
        var expectedFoods = new List<Food>
        {
            new Food
            {
                Id = "1",
                Name = "Apple",
                Type = "food",
                Category = "Fruit",
                Carbs = 25.0,
                Portion = 1.0,
                Unit = "pcs",
            },
            new Food
            {
                Id = "2",
                Name = "Banana",
                Type = "food",
                Category = "Fruit",
                Carbs = 30.0,
                Portion = 1.0,
                Unit = "pcs",
            },
        };

        _mockFoodRepository
            .Setup(x => x.GetFoodWithAdvancedFilterAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedFoods);

        // Act
        var result = await _foodService.GetFoodAsync(cancellationToken: CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(expectedFoods);
        _mockFoodRepository.Verify(
            x => x.GetFoodWithAdvancedFilterAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task GetFoodAsync_WithParameters_PagesTheRepository()
    {
        // Arrange
        var find = "Fruit";
        var count = 10;
        var skip = 5;
        var expectedFoods = new List<Food>
        {
            new Food
            {
                Id = "1",
                Name = "Apple",
                Category = "Fruit",
            },
        };

        _mockFoodRepository
            .Setup(x => x.GetFoodWithAdvancedFilterAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedFoods);

        // Act
        var result = await _foodService.GetFoodAsync(find, count, skip, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(expectedFoods);
        _mockFoodRepository.Verify(
            x => x.GetFoodWithAdvancedFilterAsync(count, skip, find, false, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task GetFoodAsync_WhenMongoDbServiceThrows_ShouldPropagateException()
    {
        // Arrange
        var expectedException = new InvalidOperationException("Database error");
        _mockFoodRepository
            .Setup(x => x.GetFoodWithAdvancedFilterAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _foodService.GetFoodAsync(cancellationToken: CancellationToken.None)
        );
        exception.Should().Be(expectedException);
    }

    #endregion

    #region GetFoodByIdAsync Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task GetFoodByIdAsync_WithValidId_ReturnsFood()
    {
        // Arrange
        var foodId = "507f1f77bcf86cd799439011";
        var expectedFood = new Food
        {
            Id = foodId,
            Name = "Apple",
            Type = "food",
            Category = "Fruit",
            Carbs = 25.0,
            Fat = 0.3,
            Protein = 0.5,
            Energy = 52.0,
            Portion = 1.0,
            Unit = "pcs",
            Gi = 2,
        };

        _mockFoodRepository
            .Setup(x => x.GetFoodByIdAsync(foodId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedFood);

        // Act
        var result = await _foodService.GetFoodByIdAsync(foodId, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(expectedFood);
        _mockFoodRepository.Verify(
            x => x.GetFoodByIdAsync(foodId, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task GetFoodByIdAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        var foodId = "507f1f77bcf86cd799439011";
        _mockFoodRepository
            .Setup(x => x.GetFoodByIdAsync(foodId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Food?)null);

        // Act
        var result = await _foodService.GetFoodByIdAsync(foodId, CancellationToken.None);

        // Assert
        result.Should().BeNull();
        _mockFoodRepository.Verify(
            x => x.GetFoodByIdAsync(foodId, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task GetFoodByIdAsync_WhenMongoDbServiceThrows_ShouldPropagateException()
    {
        // Arrange
        var foodId = "507f1f77bcf86cd799439011";
        var expectedException = new InvalidOperationException("Database error");
        _mockFoodRepository
            .Setup(x => x.GetFoodByIdAsync(foodId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _foodService.GetFoodByIdAsync(foodId, CancellationToken.None)
        );
        exception.Should().Be(expectedException);
    }

    #endregion

    #region CreateFoodAsync Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task CreateFoodAsync_WithValidFoods_ReturnsCreatedFoodsAndBroadcasts()
    {
        // Arrange
        var inputFoods = new List<Food>
        {
            new Food
            {
                Name = "Apple",
                Type = "food",
                Category = "Fruit",
                Carbs = 25.0,
                Fat = 0.3,
                Protein = 0.5,
                Energy = 52.0,
                Portion = 1.0,
                Unit = "pcs",
            },
            new Food
            {
                Name = "Banana",
                Type = "food",
                Category = "Fruit",
                Carbs = 30.0,
                Fat = 0.2,
                Protein = 1.0,
                Energy = 89.0,
                Portion = 1.0,
                Unit = "pcs",
            },
        };

        var createdFoods = inputFoods
            .Select(
                (f, i) =>
                    new Food
                    {
                        Id = $"507f1f77bcf86cd79943901{i}",
                        Name = f.Name,
                        Type = f.Type,
                        Category = f.Category,
                        Carbs = f.Carbs,
                        Fat = f.Fat,
                        Protein = f.Protein,
                        Energy = f.Energy,
                        Portion = f.Portion,
                        Unit = f.Unit,
                    }
            )
            .ToList();

        _mockFoodRepository
            .Setup(x =>
                x.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(createdFoods);

        // Act
        var result = await _foodService.CreateFoodAsync(inputFoods, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(createdFoods);
        result.Count().Should().Be(2);

        _mockFoodRepository.Verify(
            x => x.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>()),
            Times.Once
        );

        _mockSideEffects.Verify(
            x => x.OnCreatedAsync(
                "food",
                It.IsAny<IReadOnlyList<Food>>(),
                It.IsAny<WriteEffectOptions>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task CreateFoodAsync_WithEmptyList_ReturnsEmptyListAndBroadcasts()
    {
        // Arrange
        var inputFoods = new List<Food>();
        var createdFoods = new List<Food>();

        _mockFoodRepository
            .Setup(x =>
                x.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(createdFoods);

        // Act
        var result = await _foodService.CreateFoodAsync(inputFoods, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();

        _mockSideEffects.Verify(
            x => x.OnCreatedAsync(
                "food",
                It.IsAny<IReadOnlyList<Food>>(),
                It.IsAny<WriteEffectOptions>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task CreateFoodAsync_WithQuickPickFood_HandlesCorrectly()
    {
        // Arrange
        var quickPickFood = new Food
        {
            Name = "Breakfast Combo",
            Type = "quickpick",
            Category = "Meal",
            Position = 1,
            HideAfterUse = true,
            Foods = new List<QuickPickFood>
            {
                new QuickPickFood
                {
                    Name = "Toast",
                    Portion = 2.0,
                    Carbs = 30.0,
                    Unit = "slices",
                    Portions = 1.0,
                },
                new QuickPickFood
                {
                    Name = "Butter",
                    Portion = 10.0,
                    Carbs = 0.1,
                    Unit = "g",
                    Portions = 1.0,
                },
            },
        };

        var inputFoods = new List<Food> { quickPickFood };
        var createdFoods = new List<Food>
        {
            new Food
            {
                Id = "507f1f77bcf86cd799439011",
                Name = quickPickFood.Name,
                Type = quickPickFood.Type,
                Category = quickPickFood.Category,
                Position = quickPickFood.Position,
                HideAfterUse = quickPickFood.HideAfterUse,
                Foods = quickPickFood.Foods,
            },
        };

        _mockFoodRepository
            .Setup(x =>
                x.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(createdFoods);

        // Act
        var result = await _foodService.CreateFoodAsync(inputFoods, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Count().Should().Be(1);
        result.First().Type.Should().Be("quickpick");
        result.First().Foods.Should().HaveCount(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task CreateFoodAsync_WhenMongoDbServiceThrows_ShouldPropagateException()
    {
        // Arrange
        var inputFoods = new List<Food> { new Food { Name = "Test Food" } };
        var expectedException = new InvalidOperationException("Database error");

        _mockFoodRepository
            .Setup(x =>
                x.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>())
            )
            .ThrowsAsync(expectedException);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _foodService.CreateFoodAsync(inputFoods, CancellationToken.None)
        );
        exception.Should().Be(expectedException);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task CreateFoodAsync_WhenSignalRBroadcastThrows_ShouldPropagateException()
    {
        // Arrange
        var inputFoods = new List<Food> { new Food { Name = "Test Food" } };
        var createdFoods = new List<Food>
        {
            new Food { Id = "1", Name = "Test Food" },
        };
        var expectedException = new InvalidOperationException("SignalR error");

        _mockFoodRepository
            .Setup(x =>
                x.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(createdFoods);

        _mockSideEffects
            .Setup(x => x.OnCreatedAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Food>>(),
                It.IsAny<WriteEffectOptions>(),
                It.IsAny<CancellationToken>()
            ))
            .ThrowsAsync(expectedException);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _foodService.CreateFoodAsync(inputFoods, CancellationToken.None)
        );
        exception.Should().Be(expectedException);
    }

    #endregion

    #region UpdateFoodAsync Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task UpdateFoodAsync_WithValidIdAndFood_ReturnsUpdatedFoodAndBroadcasts()
    {
        // Arrange
        var foodId = "507f1f77bcf86cd799439011";
        var updateFood = new Food
        {
            Name = "Updated Apple",
            Type = "food",
            Category = "Fruit",
            Carbs = 28.0,
            Fat = 0.4,
            Protein = 0.6,
            Energy = 55.0,
            Portion = 1.2,
            Unit = "pcs",
        };

        var updatedFood = new Food
        {
            Id = foodId,
            Name = updateFood.Name,
            Type = updateFood.Type,
            Category = updateFood.Category,
            Carbs = updateFood.Carbs,
            Fat = updateFood.Fat,
            Protein = updateFood.Protein,
            Energy = updateFood.Energy,
            Portion = updateFood.Portion,
            Unit = updateFood.Unit,
        };

        _mockFoodRepository
            .Setup(x => x.UpdateFoodAsync(foodId, updateFood, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedFood);

        // Act
        var result = await _foodService.UpdateFoodAsync(foodId, updateFood, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(updatedFood);
        result!.Id.Should().Be(foodId);

        _mockFoodRepository.Verify(
            x => x.UpdateFoodAsync(foodId, updateFood, It.IsAny<CancellationToken>()),
            Times.Once
        );

        _mockSideEffects.Verify(
            x => x.OnUpdatedAsync(
                "food",
                It.IsAny<Food>(),
                It.IsAny<WriteEffectOptions>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task UpdateFoodAsync_WithNonExistentId_ReturnsNullAndDoesNotBroadcast()
    {
        // Arrange
        var foodId = "507f1f77bcf86cd799439011";
        var updateFood = new Food { Name = "Test Food" };

        _mockFoodRepository
            .Setup(x => x.UpdateFoodAsync(foodId, updateFood, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Food?)null);

        // Act
        var result = await _foodService.UpdateFoodAsync(foodId, updateFood, CancellationToken.None);

        // Assert
        result.Should().BeNull();

        _mockFoodRepository.Verify(
            x => x.UpdateFoodAsync(foodId, updateFood, It.IsAny<CancellationToken>()),
            Times.Once
        );

        _mockSideEffects.Verify(
            x => x.OnUpdatedAsync(
                It.IsAny<string>(),
                It.IsAny<Food>(),
                It.IsAny<WriteEffectOptions>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Never
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task UpdateFoodAsync_WhenMongoDbServiceThrows_ShouldPropagateException()
    {
        // Arrange
        var foodId = "507f1f77bcf86cd799439011";
        var updateFood = new Food { Name = "Test Food" };
        var expectedException = new InvalidOperationException("Database error");

        _mockFoodRepository
            .Setup(x => x.UpdateFoodAsync(foodId, updateFood, It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _foodService.UpdateFoodAsync(foodId, updateFood, CancellationToken.None)
        );
        exception.Should().Be(expectedException);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task UpdateFoodAsync_WhenSignalRBroadcastThrows_ShouldPropagateException()
    {
        // Arrange
        var foodId = "507f1f77bcf86cd799439011";
        var updateFood = new Food { Name = "Test Food" };
        var updatedFood = new Food { Id = foodId, Name = "Test Food" };
        var expectedException = new InvalidOperationException("SignalR error");

        _mockFoodRepository
            .Setup(x => x.UpdateFoodAsync(foodId, updateFood, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedFood);

        _mockSideEffects
            .Setup(x => x.OnUpdatedAsync(
                It.IsAny<string>(),
                It.IsAny<Food>(),
                It.IsAny<WriteEffectOptions>(),
                It.IsAny<CancellationToken>()
            ))
            .ThrowsAsync(expectedException);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _foodService.UpdateFoodAsync(foodId, updateFood, CancellationToken.None)
        );
        exception.Should().Be(expectedException);
    }

    #endregion

    #region DeleteFoodAsync Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task DeleteFoodAsync_WithValidId_ReturnsTrueAndBroadcasts()
    {
        // Arrange
        var foodId = "507f1f77bcf86cd799439011";

        _mockFoodRepository
            .Setup(x => x.DeleteFoodAsync(foodId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _foodService.DeleteFoodAsync(foodId, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        _mockFoodRepository.Verify(
            x => x.DeleteFoodAsync(foodId, It.IsAny<CancellationToken>()),
            Times.Once
        );

        _mockSideEffects.Verify(
            x => x.OnDeletedAsync<Food>(
                "food",
                It.IsAny<Food?>(),
                It.IsAny<WriteEffectOptions>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task DeleteFoodAsync_WithNonExistentId_ReturnsFalseAndDoesNotBroadcast()
    {
        // Arrange
        var foodId = "507f1f77bcf86cd799439011";

        _mockFoodRepository
            .Setup(x => x.DeleteFoodAsync(foodId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _foodService.DeleteFoodAsync(foodId, CancellationToken.None);

        // Assert
        result.Should().BeFalse();

        _mockFoodRepository.Verify(
            x => x.DeleteFoodAsync(foodId, It.IsAny<CancellationToken>()),
            Times.Once
        );

        _mockSideEffects.Verify(
            x => x.OnDeletedAsync<Food>(
                It.IsAny<string>(),
                It.IsAny<Food?>(),
                It.IsAny<WriteEffectOptions>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Never
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task DeleteFoodAsync_WhenMongoDbServiceThrows_ShouldPropagateException()
    {
        // Arrange
        var foodId = "507f1f77bcf86cd799439011";
        var expectedException = new InvalidOperationException("Database error");

        _mockFoodRepository
            .Setup(x => x.DeleteFoodAsync(foodId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _foodService.DeleteFoodAsync(foodId, CancellationToken.None)
        );
        exception.Should().Be(expectedException);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task DeleteFoodAsync_WhenSignalRBroadcastThrows_ShouldPropagateException()
    {
        // Arrange
        var foodId = "507f1f77bcf86cd799439011";
        var expectedException = new InvalidOperationException("SignalR error");

        _mockFoodRepository
            .Setup(x => x.DeleteFoodAsync(foodId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _mockSideEffects
            .Setup(x => x.OnDeletedAsync<Food>(
                It.IsAny<string>(),
                It.IsAny<Food?>(),
                It.IsAny<WriteEffectOptions>(),
                It.IsAny<CancellationToken>()
            ))
            .ThrowsAsync(expectedException);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _foodService.DeleteFoodAsync(foodId, CancellationToken.None)
        );
        exception.Should().Be(expectedException);
    }

    #endregion

    #region DeleteMultipleFoodAsync Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task DeleteMultipleFoodAsync_WithoutFilter_DeletesAllAndBroadcasts()
    {
        // Arrange
        var deletedCount = 5L;

        _mockFoodRepository
            .Setup(x => x.BulkDeleteFoodAsync("{}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(deletedCount);

        // Act
        var result = await _foodService.DeleteMultipleFoodAsync(null, CancellationToken.None);

        // Assert
        result.Should().Be(deletedCount);

        _mockFoodRepository.Verify(
            x => x.BulkDeleteFoodAsync("{}", It.IsAny<CancellationToken>()),
            Times.Once
        );

        _mockSideEffects.Verify(
            x => x.OnBulkDeletedAsync(
                "food",
                deletedCount,
                It.IsAny<WriteEffectOptions>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task DeleteMultipleFoodAsync_WithFilter_DeletesMatchingAndBroadcasts()
    {
        // Arrange
        var filter = "{\"category\":\"Fruit\"}";
        var deletedCount = 3L;

        _mockFoodRepository
            .Setup(x => x.BulkDeleteFoodAsync(filter, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deletedCount);

        // Act
        var result = await _foodService.DeleteMultipleFoodAsync(filter, CancellationToken.None);

        // Assert
        result.Should().Be(deletedCount);

        _mockFoodRepository.Verify(
            x => x.BulkDeleteFoodAsync(filter, It.IsAny<CancellationToken>()),
            Times.Once
        );

        _mockSideEffects.Verify(
            x => x.OnBulkDeletedAsync(
                "food",
                deletedCount,
                It.IsAny<WriteEffectOptions>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task DeleteMultipleFoodAsync_WhenNoDocumentsDeleted_ReturnsZeroAndDoesNotBroadcast()
    {
        // Arrange
        var filter = "{\"category\":\"NonExistent\"}";
        var deletedCount = 0L;

        _mockFoodRepository
            .Setup(x => x.BulkDeleteFoodAsync(filter, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deletedCount);

        // Act
        var result = await _foodService.DeleteMultipleFoodAsync(filter, CancellationToken.None);

        // Assert
        result.Should().Be(deletedCount);

        _mockFoodRepository.Verify(
            x => x.BulkDeleteFoodAsync(filter, It.IsAny<CancellationToken>()),
            Times.Once
        );

        _mockSideEffects.Verify(
            x => x.OnBulkDeletedAsync(
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<WriteEffectOptions>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Never
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task DeleteMultipleFoodAsync_WhenMongoDbServiceThrows_ShouldPropagateException()
    {
        // Arrange
        var filter = "{\"category\":\"Fruit\"}";
        var expectedException = new InvalidOperationException("Database error");

        _mockFoodRepository
            .Setup(x => x.BulkDeleteFoodAsync(filter, It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _foodService.DeleteMultipleFoodAsync(filter, CancellationToken.None)
        );
        exception.Should().Be(expectedException);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task DeleteMultipleFoodAsync_WhenSignalRBroadcastThrows_ShouldPropagateException()
    {
        // Arrange
        var filter = "{\"category\":\"Fruit\"}";
        var deletedCount = 3L;
        var expectedException = new InvalidOperationException("SignalR error");

        _mockFoodRepository
            .Setup(x => x.BulkDeleteFoodAsync(filter, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deletedCount);

        _mockSideEffects
            .Setup(x => x.OnBulkDeletedAsync(
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<WriteEffectOptions>(),
                It.IsAny<CancellationToken>()
            ))
            .ThrowsAsync(expectedException);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _foodService.DeleteMultipleFoodAsync(filter, CancellationToken.None)
        );
        exception.Should().Be(expectedException);
    }

    #endregion

    #region Business Logic and Validation Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task CreateFoodAsync_WithNutritionData_HandlesCorrectly()
    {
        // Arrange
        var nutritionFood = new Food
        {
            Name = "Almonds",
            Type = "food",
            Category = "Nuts",
            Subcategory = "Tree Nuts",
            Carbs = 21.0,
            Fat = 49.0,
            Protein = 21.0,
            Energy = 579.0,
            Portion = 100.0,
            Unit = "g",
            Gi = 1, // Low glycemic index
        };

        var inputFoods = new List<Food> { nutritionFood };
        var createdFoods = new List<Food>
        {
            new Food
            {
                Id = "507f1f77bcf86cd799439011",
                Name = nutritionFood.Name,
                Type = nutritionFood.Type,
                Category = nutritionFood.Category,
                Subcategory = nutritionFood.Subcategory,
                Carbs = nutritionFood.Carbs,
                Fat = nutritionFood.Fat,
                Protein = nutritionFood.Protein,
                Energy = nutritionFood.Energy,
                Portion = nutritionFood.Portion,
                Unit = nutritionFood.Unit,
                Gi = nutritionFood.Gi,
            },
        };

        _mockFoodRepository
            .Setup(x =>
                x.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(createdFoods);

        // Act
        var result = await _foodService.CreateFoodAsync(inputFoods, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Count().Should().Be(1);

        var createdFood = result.First();
        createdFood.Carbs.Should().Be(21.0);
        createdFood.Fat.Should().Be(49.0);
        createdFood.Protein.Should().Be(21.0);
        createdFood.Energy.Should().Be(579.0);
        createdFood.Gi.Should().Be(1);
        createdFood.Unit.Should().Be("g");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task CreateFoodAsync_WithDifferentUnits_HandlesCorrectly()
    {
        // Arrange
        var foodsWithDifferentUnits = new List<Food>
        {
            new Food
            {
                Name = "Milk",
                Unit = "ml",
                Portion = 250.0,
                Carbs = 12.0,
            },
            new Food
            {
                Name = "Bread",
                Unit = "slices",
                Portion = 2.0,
                Carbs = 30.0,
            },
            new Food
            {
                Name = "Nuts",
                Unit = "oz",
                Portion = 1.0,
                Carbs = 6.0,
            },
            new Food
            {
                Name = "Apple",
                Unit = "pcs",
                Portion = 1.0,
                Carbs = 25.0,
            },
        };

        var createdFoods = foodsWithDifferentUnits
            .Select(
                (f, i) =>
                    new Food
                    {
                        Id = $"507f1f77bcf86cd79943901{i}",
                        Name = f.Name,
                        Unit = f.Unit,
                        Portion = f.Portion,
                        Carbs = f.Carbs,
                    }
            )
            .ToList();

        _mockFoodRepository
            .Setup(x =>
                x.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(createdFoods);

        // Act
        var result = await _foodService.CreateFoodAsync(
            foodsWithDifferentUnits,
            CancellationToken.None
        );

        // Assert
        result.Should().NotBeNull();
        result.Count().Should().Be(4);

        var resultList = result.ToList();
        resultList[0].Unit.Should().Be("ml");
        resultList[1].Unit.Should().Be("slices");
        resultList[2].Unit.Should().Be("oz");
        resultList[3].Unit.Should().Be("pcs");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task CreateFoodAsync_WithGlycemicIndexValues_HandlesCorrectly()
    {
        // Arrange
        var foodsWithGi = new List<Food>
        {
            new Food
            {
                Name = "White Bread",
                Gi = 3,
                Carbs = 15.0,
            }, // High GI
            new Food
            {
                Name = "Brown Rice",
                Gi = 2,
                Carbs = 23.0,
            }, // Medium GI
            new Food
            {
                Name = "Lentils",
                Gi = 1,
                Carbs = 20.0,
            }, // Low GI
            new Food { Name = "Default Food", Carbs = 10.0 }, // Should default to 2
        };

        var createdFoods = foodsWithGi
            .Select(
                (f, i) =>
                    new Food
                    {
                        Id = $"507f1f77bcf86cd79943901{i}",
                        Name = f.Name,
                        Gi = f.Gi == 0 ? 2 : f.Gi, // Simulate default value behavior
                        Carbs = f.Carbs,
                    }
            )
            .ToList();

        _mockFoodRepository
            .Setup(x =>
                x.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(createdFoods);

        // Act
        var result = await _foodService.CreateFoodAsync(foodsWithGi, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Count().Should().Be(4);

        var resultList = result.ToList();
        resultList[0].Gi.Should().Be(3); // High
        resultList[1].Gi.Should().Be(2); // Medium
        resultList[2].Gi.Should().Be(1); // Low
        resultList[3].Gi.Should().Be(2); // Default
    }

    #endregion
}
