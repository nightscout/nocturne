using System.Net;
using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nocturne.API.Extensions;
using Nocturne.API.RateLimiting;
using Xunit;

namespace Nocturne.API.Tests.Mcp;

public class McpRateLimitTests
{
    private static readonly Func<HttpContext, string> Key =
        ServiceRegistrationExtensions.RateLimitPartitionKeys(
            new ClientRateLimitKey(new ConfigurationBuilder().Build()))[ServiceRegistrationExtensions.McpRateLimitPolicy];

    private static DefaultHttpContext Request(string host, string address)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return context;
    }

    [Fact]
    public void The_partition_is_the_tenant_host_and_the_address()
    {
        var a = Key(Request("rhys.example.test", "203.0.113.5"));

        Key(Request("RHYS.example.test", "203.0.113.5")).Should().Be(a);
        Key(Request("ann.example.test", "203.0.113.5")).Should().NotBe(a,
            "one provider address serves every tenant, so each tenant has its own allowance");
        Key(Request("rhys.example.test", "203.0.113.6")).Should().NotBe(a);
    }

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

        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        await options.OnRejected!(new OnRejectedContext { HttpContext = http, Lease = refused }, CancellationToken.None);

        http.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        int.Parse(http.Response.Headers.RetryAfter.ToString()).Should().BeInRange(1, 60);
        http.Response.Body.Position = 0;
        (await new StreamReader(http.Response.Body).ReadToEndAsync()).Should().Contain("rate_limit_exceeded");
    }
}
