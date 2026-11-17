using System.Net;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nocturne.API.Extensions;
using Nocturne.API.RateLimiting;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.API.Middleware.Handlers;

namespace Nocturne.API.Tests.Extensions;

/// <summary>
/// The limiter runs before AuthenticationMiddleware, so a partition key that reads the auth
/// context sees nothing in production and collapses into one shared bucket.
/// </summary>
public class RateLimitPartitionKeyGuardTests
{
    private static readonly OidcOptions Options = new();

    private static readonly IConfiguration Configuration = new ConfigurationBuilder().Build();

    private static readonly IReadOnlyDictionary<string, Func<HttpContext, string>> Keys =
        ServiceRegistrationExtensions.RateLimitPartitionKeys(new ClientRateLimitKey(Configuration));

    public static IEnumerable<object[]> Policies() => Keys.Keys.Select(policy => new object[] { policy });

    private static DefaultHttpContext Request(bool authenticated, Action<HttpRequest> shape)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<OidcOptions>>(new OptionsWrapper<OidcOptions>(Options));

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
        context.Request.Host = new HostString("Clock.Example.com");
        context.Request.Path = "/api/v4/example";
        shape(context.Request);

        if (authenticated)
        {
            var authContext = new AuthContext
            {
                IsAuthenticated = true,
                AuthType = AuthType.OidcToken,
                SubjectId = Guid.NewGuid(),
                TenantId = Guid.NewGuid(),
                SubjectName = "member",
            };
            context.SetAuthContext(authContext);
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, authContext.SubjectId.ToString()!),
                    new Claim(ClaimTypes.Name, authContext.SubjectName),
                    new Claim("tenant_id", authContext.TenantId.ToString()!),
                ],
                "Nocturne"));
        }

        return context;
    }

    public static IEnumerable<object[]> Shapes()
    {
        var shapes = new (string Name, Action<HttpRequest> Shape)[]
        {
            ("no credential", _ => { }),
            ("session cookie", r => r.Headers.Cookie = $"{Options.Cookie.AccessTokenName}=jwt-a"),
            ("refresh cookie", r => r.Headers.Cookie = $"{Options.Cookie.RefreshTokenName}=refresh-a"),
            ("guest cookie", r => r.Headers.Cookie = $"{GuestSessionHandler.CookieName}=guest-a"),
            ("bearer token", r => r.Headers.Authorization = "Bearer jwt-a"),
            ("query token", r => r.QueryString = QueryString.Create("token", "jwt-a")),
            ("route id", r => r.RouteValues["id"] = "face-1"),
        };
        foreach (var policy in Keys.Keys)
            foreach (var (name, shape) in shapes)
                yield return new object[] { policy, name, shape };
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void The_Key_Ignores_The_Auth_Context(string policy, string shapeName, Action<HttpRequest> shape)
    {
        var key = Keys[policy];

        var signedIn = key(Request(authenticated: true, shape));
        var anonymous = key(Request(authenticated: false, shape));

        signedIn.Should().Be(anonymous, $"{policy} ({shapeName}) must partition on the request alone");
    }

    [Fact]
    public void Every_Policy_The_Limiter_Holds_Has_A_Registered_Key()
    {
        var services = new ServiceCollection();
        services.AddApiRateLimiting(Configuration);
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        var registered = options.GetType()
            .GetProperty("PolicyMap", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(options)
            .Should().BeAssignableTo<System.Collections.IDictionary>().Subject
            .Keys.Cast<string>().ToList();

        registered.Should().NotBeEmpty();
        registered.Should().BeEquivalentTo(Keys.Keys);
    }
}
