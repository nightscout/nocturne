using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Nocturne.API.Controllers.V2;
using IAuthorizationService = Nocturne.Core.Contracts.Identity.IAuthorizationService;

namespace Nocturne.API.Tests.Controllers.V2;

/// <summary>
/// The exchange answers 401 only when the token resolves to nothing. A failure while resolving it
/// leaves the action for the exception pipeline, whose 500 envelope
/// <see cref="Middleware.ApiErrorEnvelopeTests"/> pins.
/// </summary>
public class AuthorizationControllerTokenExchangeTests
{
    private readonly Mock<IAuthorizationService> _service = new();
    private readonly AuthorizationController _controller;

    public AuthorizationControllerTokenExchangeTests()
    {
        _controller = new AuthorizationController(
            _service.Object,
            Mock.Of<ILogger<AuthorizationController>>()
        );
    }

    [Fact]
    public async Task An_unknown_token_answers_401()
    {
        _service
            .Setup(s => s.GenerateJwtFromAccessTokenAsync("noc_unknown"))
            .ReturnsAsync((AuthorizationResponse?)null);

        var result = await _controller.GenerateJwtFromAccessToken("noc_unknown");

        result.Result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task A_failing_token_lookup_is_not_answered_as_401()
    {
        _service
            .Setup(s => s.GenerateJwtFromAccessTokenAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        var exchange = () => _controller.GenerateJwtFromAccessToken("noc_uploadertoken123");

        await exchange.Should().ThrowAsync<InvalidOperationException>();
    }
}
