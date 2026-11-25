using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nocturne.API.Extensions;
using Nocturne.API.Tests.RateLimiting;
using Nocturne.API.Tests.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

public class RateLimitRetryAfterTests
{
    [Fact]
    public async Task A_refusal_says_when_to_retry()
    {
        var services = new ServiceCollection().AddApiRateLimiting(new ConfigurationBuilder().Build());
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        using var limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 1,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
        using var granted = limiter.AttemptAcquire();
        using var refused = limiter.AttemptAcquire();
        refused.IsAcquired.Should().BeFalse();

        var http = new DefaultHttpContext { RequestServices = TestRequestServices.Build(clamped: false) };
        http.Response.Body = new MemoryStream();
        await options.OnRejected!(new OnRejectedContext { HttpContext = http, Lease = refused }, CancellationToken.None);

        int.Parse(http.Response.Headers.RetryAfter.ToString()).Should().BeInRange(1, 60);
        await http.Response.ShouldBeTheRateLimitProblemAsync();
    }
}
