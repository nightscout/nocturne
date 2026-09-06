using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// Unit tests for AlexaController
/// Tests maintain 1:1 compatibility with legacy Alexa API endpoint
/// </summary>
public class AlexaControllerTests
{
    private readonly Mock<IAlexaService> _mockAlexaService;
    private readonly Mock<IAuthorizationService> _mockAuthorizationService;
    private readonly Mock<ILogger<AlexaController>> _mockLogger;
    private readonly AlexaController _controller;

    public AlexaControllerTests()
    {
        _mockAlexaService = new Mock<IAlexaService>();
        _mockAuthorizationService = new Mock<IAuthorizationService>();
        _mockLogger = new Mock<ILogger<AlexaController>>();
        _controller = new AlexaController(
            _mockAlexaService.Object,
            _mockAuthorizationService.Object,
            _mockLogger.Object
        );

        // Set up HttpContext for the controller
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                Connection = { RemoteIpAddress = IPAddress.Parse("127.0.0.1") },
            },
        };
    }

    private static AlexaRequest ValidRequest() =>
        new()
        {
            Request = new AlexaRequestDetails { Type = "LaunchRequest", Locale = "en-US" },
        };

    private static PermissionTrie PermissionTrie(params string[] permissions)
    {
        var trie = new PermissionTrie();
        trie.Add(permissions);
        return trie;
    }

    [Fact]
    public async Task HandleAlexaRequest_ValidRequest_Authorized_ReturnsOkResponse()
    {
        // Arrange
        var request = ValidRequest();

        var expectedResponse = new AlexaResponse
        {
            Version = "1.0",
            Response = new AlexaResponseDetails
            {
                OutputSpeech = new AlexaOutputSpeech
                {
                    Type = "PlainText",
                    Text = "Hello, I can help you check your blood sugar.",
                },
                ShouldEndSession = false,
            },
        };

        _controller.HttpContext.SetPermissionTrie(PermissionTrie("api:*:read"));

        _mockAlexaService
            .Setup(x => x.ProcessRequestAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        // Act
        var result = await _controller.HandleAlexaRequest(request, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<AlexaResponse>(okResult.Value);
        Assert.Equal(expectedResponse.Version, response.Version);
        Assert.Equal(
            expectedResponse.Response.OutputSpeech?.Text,
            response.Response.OutputSpeech?.Text
        );
    }

    [Fact]
    public async Task HandleAlexaRequest_ValidRequest_Unauthorized_ReturnsUnauthorized()
    {
        // Arrange: an empty permission trie grants nothing, so the request must be rejected.
        var request = ValidRequest();

        _controller.HttpContext.SetPermissionTrie(PermissionTrie());

        // Act
        var result = await _controller.HandleAlexaRequest(request, CancellationToken.None);

        // Assert
        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal("Access denied", unauthorizedResult.Value);
    }

    [Fact]
    public async Task HandleAlexaRequest_NullRequest_ReturnsBadRequest()
    {
        // Arrange
        AlexaRequest? request = null;

        // Act
        var result = await _controller.HandleAlexaRequest(request!, CancellationToken.None);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Invalid Alexa request format", badRequestResult.Value);
    }

    [Fact]
    public async Task HandleAlexaRequest_NullRequestDetails_ReturnsBadRequest()
    {
        // Arrange
        var request = new AlexaRequest { Request = null! };

        // Act
        var result = await _controller.HandleAlexaRequest(request, CancellationToken.None);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Invalid Alexa request format", badRequestResult.Value);
    }

    [Fact]
    public async Task HandleAlexaRequest_ServiceThrowsArgumentException_ReturnsBadRequest()
    {
        // Arrange
        var request = ValidRequest();

        _controller.HttpContext.SetPermissionTrie(PermissionTrie("api:*:read"));

        _mockAlexaService
            .Setup(x => x.ProcessRequestAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Invalid request format"));

        // Act
        var result = await _controller.HandleAlexaRequest(request, CancellationToken.None);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Invalid request format", badRequestResult.Value);
    }

    [Fact]
    public async Task HandleAlexaRequest_ServiceThrowsUnauthorizedException_ReturnsUnauthorized()
    {
        // Arrange
        var request = ValidRequest();

        _controller.HttpContext.SetPermissionTrie(PermissionTrie("api:*:read"));

        _mockAlexaService
            .Setup(x => x.ProcessRequestAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("Access denied"));

        // Act
        var result = await _controller.HandleAlexaRequest(request, CancellationToken.None);

        // Assert
        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal("Access denied", unauthorizedResult.Value);
    }

    [Fact]
    public async Task HandleAlexaRequest_ServiceThrowsGenericException_ReturnsAlexaErrorResponse()
    {
        // Arrange
        var request = ValidRequest();

        var errorResponse = new AlexaResponse
        {
            Version = "1.0",
            Response = new AlexaResponseDetails
            {
                OutputSpeech = new AlexaOutputSpeech
                {
                    Type = "PlainText",
                    Text = "Sorry, I'm having trouble right now. Please try again later.",
                },
                ShouldEndSession = true,
            },
        };

        _controller.HttpContext.SetPermissionTrie(PermissionTrie("api:*:read"));

        _mockAlexaService
            .Setup(x => x.ProcessRequestAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Something went wrong"));

        _mockAlexaService
            .Setup(x =>
                x.BuildSpeechletResponse(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<bool>()
                )
            )
            .Returns(errorResponse);

        // Act
        var result = await _controller.HandleAlexaRequest(request, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<AlexaResponse>(okResult.Value);
        Assert.Contains(
            "having trouble",
            response.Response.OutputSpeech?.Text ?? string.Empty
        );
    }
}
