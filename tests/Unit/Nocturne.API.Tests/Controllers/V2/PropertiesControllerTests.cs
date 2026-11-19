using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V2;
using Nocturne.Core.Contracts.Profiles;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V2;

/// <summary>
/// Unit tests for V2 PropertiesController
/// Tests the 1:1 compatibility with legacy JavaScript implementation
/// </summary>
public class PropertiesControllerTests
{
    private readonly Mock<IPropertiesService> _mockPropertiesService;
    private readonly PropertiesController _controller;

    public PropertiesControllerTests()
    {
        _mockPropertiesService = new Mock<IPropertiesService>();
        _controller = new PropertiesController(_mockPropertiesService.Object);
    }

    [Fact]
    public async Task GetAllProperties_ReturnsOkWithProperties()
    {
        // Arrange
        var expectedProperties = new Dictionary<string, object>
        {
            ["bgnow"] = new { mgdl = 120, mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            ["delta"] = new { display = "+2 mg/dl", mgdl = 2 },
        };

        _mockPropertiesService
            .Setup(x => x.GetAllPropertiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedProperties);

        // Act
        var result = await _controller.GetAllProperties(cancellationToken: CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<ActionResult<Dictionary<string, object>>>(result);
        var okValue = Assert.IsType<OkObjectResult>(okResult.Result);
        var returnedProperties = Assert.IsType<Dictionary<string, object>>(okValue.Value);

        Assert.Equal(expectedProperties.Count, returnedProperties.Count);
        Assert.True(returnedProperties.ContainsKey("bgnow"));
        Assert.True(returnedProperties.ContainsKey("delta"));
    }

    [Theory]
    [InlineData("bgnow", new[] { "bgnow" })]
    [InlineData("bgnow,delta", new[] { "bgnow", "delta" })]
    [InlineData("bgnow,delta,direction", new[] { "bgnow", "delta", "direction" })]
    public async Task GetSpecificProperties_WithValidPath_ReturnsRequestedProperties(
        string propertyPath,
        string[] expectedPropertyNames
    )
    {
        // Arrange
        var allProperties = new Dictionary<string, object>
        {
            ["bgnow"] = new { mgdl = 120 },
            ["delta"] = new { display = "+2" },
            ["direction"] = new { value = "Flat" },
        };

        var expectedProperties = expectedPropertyNames.ToDictionary(
            name => name,
            name => allProperties[name]
        );

        _mockPropertiesService
            .Setup(x =>
                x.GetPropertiesAsync(
                    It.Is<IEnumerable<string>>(props => props.SequenceEqual(expectedPropertyNames)),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedProperties);

        // Act
        var result = await _controller.GetSpecificProperties(
            propertyPath,
            cancellationToken: CancellationToken.None
        );

        // Assert
        var okResult = Assert.IsType<ActionResult<Dictionary<string, object>>>(result);
        var okValue = Assert.IsType<OkObjectResult>(okResult.Result);
        var returnedProperties = Assert.IsType<Dictionary<string, object>>(okValue.Value);

        Assert.Equal(expectedPropertyNames.Length, returnedProperties.Count);
        foreach (var expectedName in expectedPropertyNames)
        {
            Assert.True(returnedProperties.ContainsKey(expectedName));
        }
    }

    [Fact]
    public async Task GetSpecificProperties_WithEmptyPath_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.GetSpecificProperties(
            "",
            cancellationToken: CancellationToken.None
        );

        // Assert
        var badRequestResult = Assert.IsType<ActionResult<Dictionary<string, object>>>(result);
        Assert.IsType<BadRequestObjectResult>(badRequestResult.Result);
    }

    [Fact]
    public async Task GetSpecificProperties_WithWhitespacePath_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.GetSpecificProperties(
            "   ",
            cancellationToken: CancellationToken.None
        );

        // Assert
        var badRequestResult = Assert.IsType<ActionResult<Dictionary<string, object>>>(result);
        Assert.IsType<BadRequestObjectResult>(badRequestResult.Result);
    }

    [Fact]
    public async Task GetAllProperties_WithPrettyFlag_ReturnsFormattedJson()
    {
        // Arrange
        var expectedProperties = new Dictionary<string, object> { ["bgnow"] = new { mgdl = 120 } };

        _mockPropertiesService
            .Setup(x => x.GetAllPropertiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedProperties);

        // Act
        var result = await _controller.GetAllProperties(
            pretty: true,
            cancellationToken: CancellationToken.None
        );

        // Assert
        var contentResult = Assert.IsType<ActionResult<Dictionary<string, object>>>(result);
        var content = Assert.IsType<ContentResult>(contentResult.Result);

        Assert.Equal("application/json", content.ContentType);
        // Cross-platform tolerant: JSON should start with '{' and include an indented key
        Assert.StartsWith("{", content.Content);
        Assert.Contains("  \"bgnow\":", content.Content); // Should have proper indentation
    }

    [Fact]
    public async Task GetSpecificProperties_WithPrettyFlag_ReturnsFormattedJson()
    {
        // Arrange
        var expectedProperties = new Dictionary<string, object> { ["bgnow"] = new { mgdl = 120 } };

        _mockPropertiesService
            .Setup(x =>
                x.GetPropertiesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(expectedProperties);

        // Act
        var result = await _controller.GetSpecificProperties(
            "bgnow",
            pretty: true,
            cancellationToken: CancellationToken.None
        );

        // Assert
        var contentResult = Assert.IsType<ActionResult<Dictionary<string, object>>>(result);
        var content = Assert.IsType<ContentResult>(contentResult.Result);

        Assert.Equal("application/json", content.ContentType);
        // Cross-platform tolerant: JSON should start with '{' and include an indented key
        Assert.StartsWith("{", content.Content);
        Assert.Contains("  \"bgnow\":", content.Content); // Should have proper indentation
    }

    [Fact]
    public async Task GetAllProperties_WhenServiceThrows_TheExceptionEscapes()
    {
        // Arrange
        _mockPropertiesService
            .Setup(x => x.GetAllPropertiesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Service error"));

        // Act
        var act = () => _controller.GetAllProperties(cancellationToken: CancellationToken.None);

        // Assert
        await Assert.ThrowsAsync<Exception>(act);
    }

    [Fact]
    public async Task GetSpecificProperties_WhenServiceThrows_TheExceptionEscapes()
    {
        // Arrange
        _mockPropertiesService
            .Setup(x =>
                x.GetPropertiesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>())
            )
            .ThrowsAsync(new Exception("Service error"));

        // Act
        var act = () =>
            _controller.GetSpecificProperties("bgnow", cancellationToken: CancellationToken.None);

        // Assert
        await Assert.ThrowsAsync<Exception>(act);
    }
}
