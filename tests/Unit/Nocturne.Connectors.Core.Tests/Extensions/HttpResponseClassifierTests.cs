using System.Net;
using System.Net.Http;
using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Extensions;

/// <summary>
/// <see cref="HttpResponseExtensions.IsRetryableStatusCode"/> is the only verdict the connector
/// retry loops consult, so it decides both whether a request is sent again and — for the statuses
/// it declines — whether a sign-in failure is reported as refused credentials. These tests hold the
/// allow-list shape: a status is retried only when it names rate limiting or a transient fault, so
/// the classifier never replays a credential the source refused. A 401 is handled above it, by
/// re-authenticating and retrying with a fresh credential.
/// </summary>
public class HttpResponseClassifierTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public void RateLimitingAndTransientFaultsAreRetryable(HttpStatusCode status)
    {
        HttpResponseExtensions.IsRetryableStatusCode(status).Should().BeTrue();
        using var response = new HttpResponseMessage(status);
        response.IsRetryableError().Should().BeTrue();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.NotAcceptable)]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    [InlineData(HttpStatusCode.Locked)]
    [InlineData(HttpStatusCode.PreconditionRequired)]
    [InlineData(HttpStatusCode.UnavailableForLegalReasons)]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.HttpVersionNotSupported)]
    public void EverythingOutsideTheAllowListIsNotRetryable(HttpStatusCode status)
    {
        HttpResponseExtensions.IsRetryableStatusCode(status).Should().BeFalse();
        using var response = new HttpResponseMessage(status);
        response.IsRetryableError().Should().BeFalse();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public void PermanentClientErrorsAreNotRetryable(HttpStatusCode status)
    {
        HttpResponseExtensions.IsRetryableStatusCode(status).Should().BeFalse();
        using var response = new HttpResponseMessage(status);
        response.IsRetryableError().Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ARefusedCredentialIsNotReplayedByTheClassifier()
    {
        HttpResponseExtensions.IsRetryableStatusCode(HttpStatusCode.Unauthorized).Should().BeFalse();
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.IsRetryableError().Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AMissingStatusIsNotAVerdictTheClassifierCanRetryOn()
    {
        HttpResponseExtensions.IsRetryableStatusCode(null).Should().BeFalse(
            "a transport failure carries no status, and callers decide that case separately");
    }
}
