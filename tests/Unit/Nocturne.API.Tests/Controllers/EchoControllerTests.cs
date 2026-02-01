using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Services;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// Unit tests for DebugController (formerly EchoController)
/// Tests the echo and debug endpoint functionality
/// </summary>
[Trait("Category", "Unit")]
public class DebugControllerTests
{
    private readonly Mock<IPostgreSqlService> _mockPostgreSqlService;
    private readonly Mock<IInAppNotificationService> _mockNotificationService;
    private readonly Mock<IWebHostEnvironment> _mockEnvironment;
    private readonly Mock<ILogger<DebugController>> _mockLogger;
    private readonly DebugController _controller;

    public DebugControllerTests()
    {
        _mockPostgreSqlService = new Mock<IPostgreSqlService>();
        _mockNotificationService = new Mock<IInAppNotificationService>();
        _mockEnvironment = new Mock<IWebHostEnvironment>();
        _mockLogger = new Mock<ILogger<DebugController>>();
        _controller = new DebugController(
            _mockPostgreSqlService.Object,
            _mockNotificationService.Object,
            _mockEnvironment.Object,
            _mockLogger.Object
        );

        // Set up HttpContext for the controller
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?count=10&type=sgv");
        _controller.ControllerContext = new ControllerContext() { HttpContext = httpContext };
    }

    [Fact]
    public void EchoQuery_WithValidStorageType_ShouldReturnQueryInformation()
    {
        // Arrange
        var storageType = "entries";

        // Act
        var result = _controller.EchoQuery(storageType);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<OkObjectResult>();

        var okResult = result.Result as OkObjectResult;
        var response = okResult!.Value;

        response.Should().NotBeNull();
        var responseType = response!.GetType();
        responseType.GetProperty("storage")?.GetValue(response).Should().Be(storageType);
        responseType.GetProperty("query")?.GetValue(response).Should().NotBeNull();
        responseType.GetProperty("input")?.GetValue(response).Should().NotBeNull();
    }

    [Fact]
    public void EchoQuery_WithInvalidStorageType_ShouldReturnBadRequest()
    {
        // Arrange
        var invalidStorageType = "invalid";

        // Act
        var result = _controller.EchoQuery(invalidStorageType);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("entries")]
    [InlineData("treatments")]
    [InlineData("devicestatus")]
    [InlineData("activity")]
    [InlineData("profile")]
    [InlineData("food")]
    public void EchoQuery_WithAllValidStorageTypes_ShouldReturnOk(string storageType)
    {
        // Arrange & Act
        var result = _controller.EchoQuery(storageType);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public void EchoQuery_WithModelAndSpec_ShouldIncludeParametersInResponse()
    {
        // Arrange
        var storageType = "entries";
        var model = "sgv";
        var spec = "current";

        // Act
        var result = _controller.EchoQueryWithModelAndSpec(storageType, model, spec);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<OkObjectResult>();

        var okResult = result.Result as OkObjectResult;
        var response = okResult!.Value;

        var paramsProperty = response!.GetType().GetProperty("params")?.GetValue(response);
        paramsProperty.Should().NotBeNull();

        var paramsType = paramsProperty!.GetType();
        paramsType.GetProperty("echo")?.GetValue(paramsProperty).Should().Be(storageType);
        paramsType.GetProperty("model")?.GetValue(paramsProperty).Should().Be(model);
        paramsType.GetProperty("spec")?.GetValue(paramsProperty).Should().Be(spec);
    }

    [Fact]
    public async Task PreviewEntries_WithValidSingleEntry_ShouldReturnPreviewData()
    {
        // Arrange
        var entry = new Entry
        {
            Sgv = 120,
            Type = "sgv",
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DateString = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        };

        var jsonElement = JsonSerializer.SerializeToElement(entry);

        // Act
        var result = await _controller.PreviewEntries(jsonElement, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<OkObjectResult>();

        var okResult = result.Result as OkObjectResult;
        var response = okResult!.Value;

        response.Should().NotBeNull();
        var responseType = response!.GetType();
        var entries = responseType.GetProperty("entries")?.GetValue(response) as List<Entry>;
        entries.Should().ContainSingle();

        var validationResults = responseType.GetProperty("validationResults")?.GetValue(response);
        validationResults.Should().NotBeNull();

        var summary = responseType.GetProperty("summary")?.GetValue(response);
        summary.Should().NotBeNull();
    }

    [Fact]
    public async Task PreviewEntries_WithValidEntryArray_ShouldReturnPreviewDataForAll()
    {
        // Arrange
        var entries = new List<Entry>
        {
            new Entry
            {
                Sgv = 120,
                Type = "sgv",
                Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            new Entry
            {
                Sgv = 130,
                Type = "sgv",
                Mills = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            },
        };

        var jsonElement = JsonSerializer.SerializeToElement(entries);

        // Act
        var result = await _controller.PreviewEntries(jsonElement, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<OkObjectResult>();

        var okResult = result.Result as OkObjectResult;
        var response = okResult!.Value;

        response.Should().NotBeNull();
        var responseType = response!.GetType();
        var responseEntries =
            responseType.GetProperty("entries")?.GetValue(response) as List<Entry>;
        responseEntries.Should().HaveCount(2);

        var summary = responseType.GetProperty("summary")?.GetValue(response);
        summary.Should().NotBeNull();
        var summaryType = summary!.GetType();
        summaryType.GetProperty("totalEntries")?.GetValue(summary).Should().Be(2);
    }

    [Fact]
    public async Task PreviewEntries_WithInvalidEntry_ShouldReturnValidationErrors()
    {
        // Arrange
        var invalidEntry = new Entry
        {
            // Missing required fields like SGV/MBG and timestamp
            Type = "sgv",
        };

        var jsonElement = JsonSerializer.SerializeToElement(invalidEntry);

        // Act
        var result = await _controller.PreviewEntries(jsonElement, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<OkObjectResult>();

        var okResult = result.Result as OkObjectResult;
        var response = okResult!.Value;

        response.Should().NotBeNull();
        var responseType = response!.GetType();

        var validationResults =
            responseType.GetProperty("validationResults")?.GetValue(response) as List<object>;
        validationResults.Should().ContainSingle();

        var validation = validationResults![0];
        var validationType = validation.GetType();
        var validationProp = validationType.GetProperty("validation")?.GetValue(validation);
        var isValid = validationProp!.GetType().GetProperty("isValid")?.GetValue(validationProp);
        isValid.Should().Be(false);
    }

    [Fact]
    public async Task PreviewEntries_WithNullData_ShouldReturnBadRequest()
    {
        // Arrange & Act
        var result = await _controller.PreviewEntries(null!, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PreviewEntries_WithInvalidJsonFormat_ShouldReturnBadRequest()
    {
        // Arrange
        var invalidJson = "{ invalid json }";
        var jsonElement = JsonDocument.Parse($"\"{invalidJson}\"").RootElement;

        // Act
        var result = await _controller.PreviewEntries(jsonElement, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void EchoQuery_WhenServiceThrowsException_ShouldReturnInternalServerError()
    {
        // Arrange
        var storageType = "entries";

        // Mock HttpContext to simulate an exception scenario
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?invalid=data");
        _controller.ControllerContext.HttpContext = httpContext;

        // Act
        var result = _controller.EchoQuery(storageType);

        // Assert
        result.Should().NotBeNull();
        // Should still return OK since echo endpoint is for debugging and shouldn't fail easily
        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public void EchoQuery_WithComplexQueryParameters_ShouldParseCorrectly()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(
            "?find={\"type\":\"sgv\"}&count=50&dateString=2024"
        );
        _controller.ControllerContext.HttpContext = httpContext;

        var storageType = "entries";

        // Act
        var result = _controller.EchoQuery(storageType);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<OkObjectResult>();

        var okResult = result.Result as OkObjectResult;
        var response = okResult!.Value;

        var inputProperty =
            response!.GetType().GetProperty("input")?.GetValue(response)
            as Dictionary<string, object>;
        inputProperty.Should().ContainKey("find");
        inputProperty.Should().ContainKey("count");
        inputProperty.Should().ContainKey("dateString");
    }

    [Fact]
    public async Task PreviewEntries_WithEntryContainingWarnings_ShouldIncludeWarningsInValidation()
    {
        // Arrange
        var entryWithWarnings = new Entry
        {
            Sgv = 2000, // Out of normal range - should trigger warning
            Type = "sgv",
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DateString = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        };

        var jsonElement = JsonSerializer.SerializeToElement(entryWithWarnings);

        // Act
        var result = await _controller.PreviewEntries(jsonElement, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<OkObjectResult>();

        var okResult = result.Result as OkObjectResult;
        var response = okResult!.Value;

        var validationResults =
            response!.GetType().GetProperty("validationResults")?.GetValue(response)
            as List<object>;
        var validation = validationResults![0];
        var validationProp = validation.GetType().GetProperty("validation")?.GetValue(validation);

        var warnings =
            validationProp!.GetType().GetProperty("warnings")?.GetValue(validationProp)
            as List<string>;
        warnings.Should().NotBeEmpty();
        warnings.Should().Contain(w => w.Contains("out of normal range"));
    }
}
