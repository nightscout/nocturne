using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Middleware.Handlers;
using Nocturne.Core.Contracts.Auth;
using Xunit;
using Subject = Nocturne.Core.Models.Authorization.Subject;

namespace Nocturne.API.Tests.Middleware.Handlers;

public class AccessTokenHandlerTests
{
    private readonly Mock<ISubjectService> _subjectService = new();
    private readonly AccessTokenHandler _handler;

    public AccessTokenHandlerTests()
    {
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider
            .Setup(p => p.GetService(typeof(ISubjectService)))
            .Returns(_subjectService.Object);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        _handler = new AccessTokenHandler(
            scopeFactory.Object,
            NullLogger<AccessTokenHandler>.Instance);
    }

    private static HttpContext CreateContext(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?token={token}");
        return context;
    }

    [Fact]
    public async Task AuthenticateAsync_UnknownToken_ReturnsSkip()
    {
        // A request can carry an unrecognized ?token= alongside a valid api-secret; the
        // chain must keep going so ApiKeyHandler (priority 400) still gets to authenticate
        // it, as classic Nightscout does. Failing here would stop the chain.
        _subjectService
            .Setup(s => s.GetSubjectByAccessTokenHashAsync(It.IsAny<string>()))
            .ReturnsAsync((Subject?)null);

        var result = await _handler.AuthenticateAsync(CreateContext("someone-a1b2c3d4e5f6g7h8"));

        Assert.True(result.ShouldSkip);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AuthenticateAsync_MalformedToken_ReturnsSkip()
    {
        // No dash: not the legacy name-hash shape, so this handler doesn't own it.
        var result = await _handler.AuthenticateAsync(CreateContext("notanaccesstoken"));

        Assert.True(result.ShouldSkip);
        _subjectService.Verify(
            s => s.GetSubjectByAccessTokenHashAsync(It.IsAny<string>()),
            Times.Never);
    }
}
