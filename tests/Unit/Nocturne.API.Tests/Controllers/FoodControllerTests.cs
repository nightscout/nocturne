using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Services;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Unit tests for FoodController
/// Tests the controller logic with mocked dependencies
/// </summary>
[Trait("Category", "Unit")]
public class FoodControllerTests
{
    private readonly Mock<IPostgreSqlService> _mockPostgreSqlService;
    private readonly Mock<ILogger<FoodController>> _mockLogger;
    private readonly FoodController _controller;

    public FoodControllerTests()
    {
        _mockPostgreSqlService = new Mock<IPostgreSqlService>();
        _mockLogger = new Mock<ILogger<FoodController>>();
        _controller = new FoodController(_mockPostgreSqlService.Object, _mockLogger.Object);

        // Set up HttpContext for the controller
        var httpContext = new DefaultHttpContext();
        _controller.ControllerContext = new ControllerContext() { HttpContext = httpContext };
    }

    [Fact]
    public async Task GetFood_WhenFoodExists_ShouldReturnFood()
    {
        // Arrange
        var expectedFood = new List<Food>
        {
            new Food
            {
                Id = "507f1f77bcf86cd799439011",
                Type = "food",
                Category = "Dairy",
                Name = "Milk",
                Portion = 100,
                Unit = "ml",
                Carbs = 5,
            },
            new Food
            {
                Id = "507f1f77bcf86cd799439012",
                Type = "quickpick",
                Name = "Quick Meal",
                Foods = new List<QuickPickFood>
                {
                    new QuickPickFood
                    {
                        Name = "Quick Meal",
                        Carbs = 30,
                        Portion = 1,
                        Unit = "serving",
                        Portions = 1,
                    },
                },
            },
        };

        _mockPostgreSqlService
            .Setup(x => x.GetFoodAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedFood);

        // Act
        var result = await _controller.GetFood(CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var food = okResult.Value.Should().BeOfType<Food[]>().Subject;
        food.Should().HaveCount(2);
        food[0].Name.Should().Be("Milk");
        food[0].Type.Should().Be("food");
        food[1].Type.Should().Be("quickpick");
    }

    [Fact]
    public async Task GetFood_WhenNoFoodExists_ShouldReturnEmptyArray()
    {
        // Arrange
        _mockPostgreSqlService
            .Setup(x => x.GetFoodAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Food>());

        // Act
        var result = await _controller.GetFood(CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var food = okResult.Value.Should().BeOfType<Food[]>().Subject;
        food.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFood_WhenServiceThrows_ShouldReturnInternalServerError()
    {
        // Arrange
        _mockPostgreSqlService
            .Setup(x => x.GetFoodAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act
        var result = await _controller.GetFood(CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task GetFoodJson_WhenFoodExists_ShouldReturnFood()
    {
        // Arrange
        var expectedFood = new List<Food>
        {
            new Food
            {
                Id = "507f1f77bcf86cd799439011",
                Type = "food",
                Name = "Test Food",
            },
        };

        _mockPostgreSqlService
            .Setup(x => x.GetFoodAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedFood);

        // Act
        var result = await _controller.GetFoodJson(CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var food = okResult.Value.Should().BeOfType<Food[]>().Subject;
        food.Should().HaveCount(1);
        food[0].Name.Should().Be("Test Food");
    }

    [Fact]
    public async Task GetFoodByType_WithRegularType_ShouldReturnRegularFoodOnly()
    {
        // Arrange
        var expectedFood = new List<Food>
        {
            new Food
            {
                Id = "507f1f77bcf86cd799439011",
                Type = "food",
                Name = "Regular Food",
            },
        };

        _mockPostgreSqlService
            .Setup(x => x.GetFoodByTypeAsync("food", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedFood);
        // Act
        var result = await _controller.GetRegularFood(CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var food = okResult.Value.Should().BeOfType<Food[]>().Subject;
        food.Should().HaveCount(1);
        food[0].Type.Should().Be("food");
    }

    [Fact]
    public async Task GetFoodByType_WithQuickpicksType_ShouldReturnQuickpicksOnly()
    {
        // Arrange
        var expectedFood = new List<Food>
        {
            new Food
            {
                Id = "507f1f77bcf86cd799439011",
                Type = "quickpick",
                Name = "Quick Food",
            },
        };

        _mockPostgreSqlService
            .Setup(x => x.GetFoodByTypeAsync("quickpick", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedFood);
        // Act
        var result = await _controller.GetQuickPickFood(CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var food = okResult.Value.Should().BeOfType<Food[]>().Subject;
        food.Should().HaveCount(1);
        food[0].Type.Should().Be("quickpick");
    }

    [Fact]
    public async Task GetFoodById_WhenFoodExists_ShouldReturnFood()
    {
        // Arrange
        var expectedFood = new Food
        {
            Id = "507f1f77bcf86cd799439011",
            Type = "food",
            Name = "Test Food",
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.GetFoodByIdAsync("507f1f77bcf86cd799439011", It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(expectedFood);

        // Act
        var result = await _controller.GetFoodById(
            "507f1f77bcf86cd799439011",
            CancellationToken.None
        );

        // Assert
        result.Should().NotBeNull();
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var food = okResult.Value.Should().BeOfType<Food>().Subject;
        food.Name.Should().Be("Test Food");
        food.Id.Should().Be("507f1f77bcf86cd799439011");
    }

    [Fact]
    public async Task GetFoodById_WhenFoodNotFound_ShouldReturnNotFound()
    {
        // Arrange
        _mockPostgreSqlService
            .Setup(x => x.GetFoodByIdAsync("nonexistent", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Food?)null);

        // Act
        var result = await _controller.GetFoodById("nonexistent", CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task CreateFood_WithValidFood_ShouldReturnCreatedFood()
    {
        // Arrange
        var newFood = new Food
        {
            Type = "food",
            Name = "New Food",
            Category = "Test",
            Carbs = 10,
        };

        var createdFood = new Food
        {
            Id = "507f1f77bcf86cd799439011",
            Type = "food",
            Name = "New Food",
            Category = "Test",
            Carbs = 10,
        };

        _mockPostgreSqlService
            .Setup(x => x.CreateFoodAsync(It.IsAny<List<Food>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Food> { createdFood });

        // Convert Food to JsonElement
        var jsonFood = JsonSerializer.SerializeToElement(newFood);

        // Act
        var result = await _controller.CreateFood(jsonFood, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var food = okResult.Value.Should().BeOfType<Food[]>().Subject;
        food.Should().HaveCount(1);
        food[0].Name.Should().Be("New Food");
        food[0].Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateFood_WithFoodArray_ShouldReturnCreatedFoodArray()
    {
        // Arrange
        var newFoods = new Food[]
        {
            new Food { Type = "food", Name = "Food 1" },
            new Food { Type = "food", Name = "Food 2" },
        };

        var createdFoods = new List<Food>
        {
            new Food
            {
                Id = "507f1f77bcf86cd799439011",
                Type = "food",
                Name = "Food 1",
            },
            new Food
            {
                Id = "507f1f77bcf86cd799439012",
                Type = "food",
                Name = "Food 2",
            },
        };

        _mockPostgreSqlService
            .Setup(x => x.CreateFoodAsync(It.IsAny<List<Food>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(createdFoods);

        // Convert Food array to JsonElement
        var jsonFoods = JsonSerializer.SerializeToElement(newFoods);

        // Act
        var result = await _controller.CreateFood(jsonFoods, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var foods = okResult.Value.Should().BeOfType<Food[]>().Subject;
        foods.Should().HaveCount(2);
        foods[0].Name.Should().Be("Food 1");
        foods[1].Name.Should().Be("Food 2");
    }

    [Fact]
    public async Task UpdateFood_WhenFoodExists_ShouldReturnUpdatedFood()
    {
        // Arrange
        var updateFood = new Food
        {
            Type = "food",
            Name = "Updated Food",
            Carbs = 15,
        };

        var updatedFood = new Food
        {
            Id = "507f1f77bcf86cd799439011",
            Type = "food",
            Name = "Updated Food",
            Carbs = 15,
        };

        _mockPostgreSqlService
            .Setup(x =>
                x.UpdateFoodAsync(
                    "507f1f77bcf86cd799439011",
                    updateFood,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(updatedFood);

        // Act
        var result = await _controller.UpdateFood(
            "507f1f77bcf86cd799439011",
            updateFood,
            CancellationToken.None
        );

        // Assert
        result.Should().NotBeNull();
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var food = okResult.Value.Should().BeOfType<Food>().Subject;
        food.Name.Should().Be("Updated Food");
        food.Carbs.Should().Be(15);
    }

    [Fact]
    public async Task UpdateFood_WhenFoodNotFound_ShouldReturnNotFound()
    {
        // Arrange
        var updateFood = new Food { Name = "Updated Food" };

        _mockPostgreSqlService
            .Setup(x => x.UpdateFoodAsync("nonexistent", updateFood, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Food?)null);

        // Act
        var result = await _controller.UpdateFood(
            "nonexistent",
            updateFood,
            CancellationToken.None
        );

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task DeleteFood_WhenFoodExists_ShouldReturnNoContent()
    {
        // Arrange
        _mockPostgreSqlService
            .Setup(x =>
                x.DeleteFoodAsync("507f1f77bcf86cd799439011", It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(true);

        // Act
        var result = await _controller.DeleteFood(
            "507f1f77bcf86cd799439011",
            CancellationToken.None
        );

        // Assert
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeleteFood_WhenFoodNotFound_ShouldReturnNotFound()
    {
        // Arrange
        _mockPostgreSqlService
            .Setup(x => x.DeleteFoodAsync("nonexistent", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.DeleteFood("nonexistent", CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }
}
