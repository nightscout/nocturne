using System.Net;
using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Extensions;

/// <summary>
/// The connector retry loop branches on <see cref="HttpRequestException.StatusCode"/> (re-authenticate
/// on 401, back off on a retryable status), and the body is usually the only account of why the source
/// refused. A failure that drops either is retried wrongly or reported blind.
/// </summary>
public class EnsureSuccessWithBodyTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task EnsureSuccessWithBodyAsync_FailedResponse_KeepsTheStatusCodeAndBody(HttpStatusCode status)
    {
        using var response = new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"error\":\"account locked\"}"),
        };

        var thrown = await FluentActions.Awaiting(() => response.EnsureSuccessWithBodyAsync())
            .Should().ThrowAsync<HttpRequestException>();

        thrown.Which.StatusCode.Should().Be(status);
        thrown.Which.Message.Should().Contain("{\"error\":\"account locked\"}")
            .And.Contain(((int)status).ToString());
    }

    [Fact]
    public async Task EnsureSuccessWithBodyAsync_SuccessfulResponse_DoesNotThrow()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };

        await FluentActions.Awaiting(() => response.EnsureSuccessWithBodyAsync())
            .Should().NotThrowAsync();
    }
}
