using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Nocturne.API.Extensions;
using Xunit;

namespace Nocturne.API.Tests.Extensions;

[Trait("Category", "Unit")]
public class HttpResponseExtensionsTests
{
    [Theory]
    [InlineData(1200, "2")]
    [InlineData(2000, "2")]
    [InlineData(1, "1")]
    [InlineData(60_000, "60")]
    public void RetryAfterIsWholeSecondsRoundedUp(int milliseconds, string expected)
    {
        var response = new DefaultHttpContext().Response;

        response.SetRetryAfter(TimeSpan.FromMilliseconds(milliseconds));

        response.Headers.RetryAfter.ToString().Should().Be(expected);
    }
}
