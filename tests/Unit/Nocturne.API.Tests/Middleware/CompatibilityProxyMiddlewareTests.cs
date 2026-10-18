using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using FluentAssertions;
using Nocturne.API.Configuration;
using Nocturne.API.Middleware;
using Nocturne.API.Models.Compatibility;
using Nocturne.API.Services.Compatibility;
using Nocturne.API.Tests.TestDoubles;
using Microsoft.Extensions.Hosting;

namespace Nocturne.API.Tests.Middleware;

public class CompatibilityProxyMiddlewareTests
{
    private readonly Mock<IRequestCloningService> _cloningServiceMock;
    private readonly Mock<IRequestForwardingService> _forwardingServiceMock;
    private readonly Mock<IResponseComparisonService> _comparisonServiceMock;
    private readonly Mock<IDiscrepancyPersistenceService> _persistenceServiceMock;
    private readonly ILogger<CompatibilityProxyMiddleware> _logger;

    public CompatibilityProxyMiddlewareTests()
    {
        _cloningServiceMock = new Mock<IRequestCloningService>();
        _forwardingServiceMock = new Mock<IRequestForwardingService>();
        _comparisonServiceMock = new Mock<IResponseComparisonService>();
        _persistenceServiceMock = new Mock<IDiscrepancyPersistenceService>();
        _logger = NullLogger<CompatibilityProxyMiddleware>.Instance;

        // Default setups
        _cloningServiceMock
            .Setup(s => s.CloneRequestAsync(It.IsAny<HttpRequest>()))
            .ReturnsAsync(new ClonedRequest { Method = "GET", Path = "/api/v1/entries.json" });

        _forwardingServiceMock
            .Setup(s => s.ForwardToNightscoutAsync(It.IsAny<ClonedRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TargetResponse { StatusCode = 200, IsSuccess = true, Target = "Nightscout" });

        _comparisonServiceMock
            .Setup(s => s.CompareResponsesAsync(
                It.IsAny<TargetResponse?>(),
                It.IsAny<TargetResponse?>(),
                It.IsAny<string>(),
                It.IsAny<string?>()))
            .ReturnsAsync(new ResponseComparisonResult());

        _persistenceServiceMock
            .Setup(s => s.StoreAnalysisAsync(
                It.IsAny<ResponseComparisonResult>(),
                It.IsAny<CompatibilityProxyResponse>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
    }

    private (CompatibilityProxyMiddleware middleware, DefaultHttpContext context) CreateMiddleware(
        bool proxyEnabled,
        RequestDelegate? next = null,
        string method = "GET",
        string path = "/api/v1/entries.json")
    {
        var config = Options.Create(new CompatibilityProxyConfiguration { Enabled = proxyEnabled });

        // Build a service collection that mimics DI
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton(_cloningServiceMock.Object);
        services.AddSingleton(_forwardingServiceMock.Object);
        services.AddSingleton(_comparisonServiceMock.Object);
        services.AddSingleton(_persistenceServiceMock.Object);
        services.AddSingleton<IRequestPathRedactor>(CredentialRouteRedactor.Create());
        // Register as their interfaces so scoped resolution works
        services.AddSingleton<IOptions<CompatibilityProxyConfiguration>>(config);
        services.AddScoped<IRequestForwardingService>(_ => _forwardingServiceMock.Object);
        services.AddScoped<IResponseComparisonService>(_ => _comparisonServiceMock.Object);
        services.AddScoped<IDiscrepancyPersistenceService>(_ => _persistenceServiceMock.Object);

        var serviceProvider = services.BuildServiceProvider();

        next ??= async ctx =>
        {
            var body = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(body);
        };

        var middleware = new CompatibilityProxyMiddleware(next, _logger);

        var context = new DefaultHttpContext
        {
            RequestServices = serviceProvider,
        };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        return (middleware, context);
    }

    [Fact]
    public async Task GetRequest_OnV1Path_ForwardsToNightscoutInBackground()
    {
        // Arrange
        var (middleware, context) = CreateMiddleware(proxyEnabled: true);

        // Act
        await middleware.InvokeAsync(context);

        // Allow background task to complete
        await Task.Delay(500);

        // Assert — the forwarding service should have been called
        _forwardingServiceMock.Verify(
            s => s.ForwardToNightscoutAsync(It.IsAny<ClonedRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The upstream still needs the real request, but the stored analysis is readable through the
    /// compatibility API and must not carry a credential path segment.
    /// </summary>
    [Fact]
    public async Task CredentialRoute_ForwardsTheRealRequest_AndStoresTheRedactedPath()
    {
        const string path = "/api/v2/authorization/request/noc_credentialthatmustnotbestored";
        var cloned = new ClonedRequest { Method = "GET", Path = path };
        _cloningServiceMock.Setup(s => s.CloneRequestAsync(It.IsAny<HttpRequest>())).ReturnsAsync(cloned);
        var stored = new TaskCompletionSource<string>();
        _persistenceServiceMock
            .Setup(s => s.StoreAnalysisAsync(
                It.IsAny<ResponseComparisonResult>(),
                It.IsAny<CompatibilityProxyResponse>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<ResponseComparisonResult, CompatibilityProxyResponse, string, string, CancellationToken>(
                (_, _, _, requestPath, _) => stored.TrySetResult(requestPath))
            .ReturnsAsync(Guid.NewGuid());
        var (middleware, context) = CreateMiddleware(proxyEnabled: true, path: path);

        await middleware.InvokeAsync(context);

        (await stored.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(CredentialRouteRedactor.TokenExchangePath);
        _forwardingServiceMock.Verify(
            s => s.ForwardToNightscoutAsync(cloned, It.IsAny<CancellationToken>()),
            Times.Once);
        _comparisonServiceMock.Verify(
            s => s.CompareResponsesAsync(
                It.IsAny<TargetResponse?>(),
                It.IsAny<TargetResponse?>(),
                It.IsAny<string>(),
                It.Is<string?>(p => p != null && p.Contains("noc_"))),
            Times.Never);
    }

    [Fact]
    public async Task PostRequest_OnV1Path_DoesNotForward()
    {
        // Arrange
        var (middleware, context) = CreateMiddleware(proxyEnabled: true, method: "POST", path: "/api/v1/entries");

        // Act
        await middleware.InvokeAsync(context);
        await Task.Delay(200);

        // Assert
        _forwardingServiceMock.Verify(
            s => s.ForwardToNightscoutAsync(It.IsAny<ClonedRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetRequest_OnV4Path_DoesNotForward()
    {
        // Arrange
        var (middleware, context) = CreateMiddleware(proxyEnabled: true, path: "/api/v4/chart-data");

        // Act
        await middleware.InvokeAsync(context);
        await Task.Delay(200);

        // Assert
        _forwardingServiceMock.Verify(
            s => s.ForwardToNightscoutAsync(It.IsAny<ClonedRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProxyDisabled_DoesNotForward()
    {
        // Arrange
        var (middleware, context) = CreateMiddleware(proxyEnabled: false);

        // Act
        await middleware.InvokeAsync(context);
        await Task.Delay(200);

        // Assert
        _forwardingServiceMock.Verify(
            s => s.ForwardToNightscoutAsync(It.IsAny<ClonedRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResponseOver10MB_SkipsForwarding()
    {
        // Arrange
        var largeBody = new byte[11 * 1024 * 1024]; // 11 MB
        RequestDelegate next = async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(largeBody);
        };

        var (middleware, context) = CreateMiddleware(proxyEnabled: true, next: next);

        // Act
        await middleware.InvokeAsync(context);
        await Task.Delay(200);

        // Assert
        _forwardingServiceMock.Verify(
            s => s.ForwardToNightscoutAsync(It.IsAny<ClonedRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NocturneResponse_IsReturnedCorrectly_RegardlessOfProxy()
    {
        // Arrange
        var expectedBody = "{\"entries\":[]}";
        RequestDelegate next = async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(expectedBody));
        };

        var (middleware, context) = CreateMiddleware(proxyEnabled: true, next: next);

        // Act
        await middleware.InvokeAsync(context);

        // Assert — read back what was written to the original response body
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var actualBody = await reader.ReadToEndAsync();

        actualBody.Should().Be(expectedBody);
        context.Response.StatusCode.Should().Be(200);
        context.Response.ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task ForwardingFailure_DoesNotAffectResponse()
    {
        // Arrange — make the forwarding service throw
        _forwardingServiceMock
            .Setup(s => s.ForwardToNightscoutAsync(It.IsAny<ClonedRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Nightscout is down"));

        var expectedBody = "{\"status\":\"ok\"}";
        RequestDelegate next = async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(expectedBody));
        };

        var (middleware, context) = CreateMiddleware(proxyEnabled: true, next: next);

        // Act — should not throw
        await middleware.InvokeAsync(context);
        await Task.Delay(500); // let background task fail gracefully

        // Assert — response is still correct
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var actualBody = await reader.ReadToEndAsync();

        actualBody.Should().Be(expectedBody);
        context.Response.StatusCode.Should().Be(200);
    }
}
