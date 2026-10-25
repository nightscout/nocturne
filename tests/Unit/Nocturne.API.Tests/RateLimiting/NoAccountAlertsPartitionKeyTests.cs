using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Extensions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.RateLimiting;

/// <summary>
/// The limiter runs before authentication, so the key is built from the request alone: an auth
/// context, which the pipeline has not set yet, must not change it.
/// </summary>
[Trait("Category", "Unit")]
public class NoAccountAlertsPartitionKeyTests
{
    [Fact]
    public void EachClockFaceOfAHost_HasItsOwnWindow()
    {
        Key(Request("Kitchen.Example.com", id: "a")).Should().Be(Key(Request("kitchen.example.com", id: "a")));
        Key(Request("kitchen.example.com", id: "a")).Should().NotBe(Key(Request("kitchen.example.com", id: "b")));
    }

    [Fact]
    public void EachCredential_HasItsOwnWindow()
    {
        var first = Request("t.example.com");
        first.Request.Headers.Authorization = "Bearer one";
        var second = Request("t.example.com");
        second.Request.Headers.Authorization = "Bearer two";
        var guest = Request("t.example.com");
        guest.Request.Headers.Cookie = "nocturne-guest-session=abc";

        new[] { Key(first), Key(second), Key(guest), Key(Request("t.example.com")) }
            .Should().OnlyHaveUniqueItems();
        Key(first).Should().NotContain("one", "no credential is kept as a key");
    }

    [Fact]
    public void AnAnonymousCaller_SharesOneWindowPerHost_AndCannotSpendAnotherHosts()
    {
        Key(Request("t.example.com")).Should().Be(Key(Request("t.example.com")));
        Key(Request("t.example.com")).Should().NotBe(Key(Request("token.share.example.com")));
    }

    [Fact]
    public void TheAuthContextAndClientAddress_AreNotPartOfTheKey()
    {
        var plain = Request("t.example.com");
        var resolved = Request("t.example.com");
        resolved.SetAuthContext(new AuthContext { IsAuthenticated = true, SubjectId = Guid.NewGuid() });
        resolved.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.2");

        Key(resolved).Should().Be(Key(plain));
    }

    private static string Key(HttpContext context) =>
        ServiceRegistrationExtensions.NoAccountAlertsPartitionKey(context);

    private static DefaultHttpContext Request(string host, string? id = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };
        context.Request.Host = new HostString(host);
        if (id is not null)
            context.Request.RouteValues["id"] = id;
        return context;
    }
}
