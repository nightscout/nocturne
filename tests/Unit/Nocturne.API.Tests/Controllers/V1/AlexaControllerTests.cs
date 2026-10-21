using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Configuration;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
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

    private void Grant(params string[] scopes) =>
        _controller.HttpContext.SetGrantedScopes(new HashSet<string>(scopes));

    private void GrantLegacy(params string[] permissions) =>
        Grant([.. ScopeTranslator.FromPermissions(permissions)]);

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

        GrantLegacy("api:*:read");

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
        var request = ValidRequest();

        Grant();

        // Act
        var result = await _controller.HandleAlexaRequest(request, CancellationToken.None);

        // Assert
        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal("Access denied", unauthorizedResult.Value);
    }

    public static TheoryData<string, string[]> CredentialsThatReadGlucose() =>
        new()
        {
            { "Viewer member", [.. RoleSeeds.Permissions[RoleSeeds.Viewer]] },
            { "Nightscout readable token", [.. ScopeTranslator.FromPermissions(["readable"])] },
            { "glucose.readwrite uploader", [Scope.GlucoseReadWrite] },
            { "guest at the guest ceiling", [.. Scope.AllowedGuestScopes] },
            { "public share at the share ceiling", [.. Scope.PublicShareScopes] },
            { "default public share", [.. Scope.DefaultPublicShareScopes] },
        };

    [Theory]
    [MemberData(nameof(CredentialsThatReadGlucose))]
    public async Task HandleAlexaRequest_CredentialThatReadsGlucose_ReturnsOk(
        string credential,
        string[] scopes
    )
    {
        _ = credential;
        var request = ValidRequest();
        Assert.DoesNotContain(Scope.FullAccess, scopes);
        Grant(scopes);
        _mockAlexaService
            .Setup(x => x.ProcessRequestAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlexaResponse { Version = "1.0" });

        var result = await _controller.HandleAlexaRequest(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    public static TheoryData<string, string[]> CredentialsThatCannotReadGlucose() =>
        new()
        {
            { "no scopes", [] },
            { "everything but glucose", [.. ScopeTranslator
                .FromPermissions(["readable"])
                .Where(scope => scope is not (Scope.GlucoseRead or Scope.GlucoseReadWrite))] },
            { "guest without glucose", [.. Scope.AllowedGuestScopes.Where(scope => scope != Scope.GlucoseRead)] },
            { "share without glucose", [.. Scope.PublicShareScopes.Where(scope => scope != Scope.GlucoseRead)] },
        };

    [Theory]
    [MemberData(nameof(CredentialsThatCannotReadGlucose))]
    public async Task HandleAlexaRequest_CredentialThatCannotReadGlucose_ReturnsUnauthorized(
        string credential,
        string[] scopes
    )
    {
        _ = credential;
        Grant(scopes);

        var result = await _controller.HandleAlexaRequest(ValidRequest(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        _mockAlexaService.Verify(
            x => x.ProcessRequestAsync(It.IsAny<AlexaRequest>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Theory]
    [InlineData("LaunchRequest")]
    [InlineData("HelpIntent")]
    public async Task OpenSessionResponse_KeepsShouldEndSessionFalseOnTheNightscoutWire(string kind)
    {
        var service = new AlexaService(
            Mock.Of<IEntryStore>(),
            Mock.Of<IPropertiesService>(),
            Mock.Of<IDDataService>(),
            new HttpContextAccessor(),
            Mock.Of<IShareAppearanceReader>(),
            Mock.Of<ITenantAccessor>(),
            TimeProvider.System,
            Mock.Of<ILogger<AlexaService>>()
        );
        var response = kind == "LaunchRequest"
            ? await service.HandleLaunchRequestAsync("en")
            : await service.HandleIntentRequestAsync(
                new AlexaIntent { Name = "AMAZON.HelpIntent" },
                "en"
            );

        var json = JsonSerializer.Serialize(response, NightscoutJsonOptions.Create());

        Assert.Contains("\"shouldEndSession\":false", json);
    }

    [Fact]
    public void HandleAlexaRequest_IsServedAtApiV1Alexa()
    {
        var route = typeof(AlexaController).GetCustomAttribute<RouteAttribute>();
        var post = typeof(AlexaController)
            .GetMethod(nameof(AlexaController.HandleAlexaRequest))!
            .GetCustomAttribute<HttpPostAttribute>();

        Assert.Equal("api/v1/[controller]", route?.Template);
        Assert.NotNull(post);
        Assert.Null(post.Template);
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

        GrantLegacy("api:*:read");

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

        GrantLegacy("api:*:read");

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

        GrantLegacy("api:*:read");

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
