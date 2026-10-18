using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nocturne.API.Controllers.V4.Health;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Xunit;

namespace Nocturne.API.Tests.Health.GoogleHealth;

public class GoogleHealthPlatformTests
{
    [Theory]
    [InlineData("alice")]
    [InlineData(null)]
    public void State_round_trips_the_tenant_slug(string? slug)
    {
        var state = new GoogleHealthOAuthState(new EphemeralDataProtectionProvider());

        Assert.Equal(slug, state.TryReadTenantSlug(state.Protect(slug, DateTimeOffset.UtcNow.AddMinutes(10))));
    }

    [Fact]
    public void An_expired_state_is_unreadable()
    {
        var state = new GoogleHealthOAuthState(new EphemeralDataProtectionProvider());

        Assert.Null(state.TryReadTenantSlug(state.Protect("alice", DateTimeOffset.UtcNow.AddSeconds(-1))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-protected-payload")]
    public void A_state_this_instance_did_not_issue_is_unreadable(string value) =>
        Assert.Null(new GoogleHealthOAuthState(new EphemeralDataProtectionProvider()).TryReadTenantSlug(value));

    [Theory]
    [InlineData("nocturne.run", "", "https://nocturne.run/api/v4/google-health/oauth/callback")]
    [InlineData("nocturne.localhost:1612", "", "https://nocturne.localhost:1612/api/v4/google-health/oauth/callback")]
    [InlineData("", "", "")]
    [InlineData("nocturne.run", "https://health.example/api/v4/google-health/oauth/callback",
        "https://health.example/api/v4/google-health/oauth/callback")]
    [InlineData("", "https://health.example/api/v4/google-health/oauth/callback",
        "https://health.example/api/v4/google-health/oauth/callback")]
    public void Redirect_uri_defaults_to_the_apex_callback_only_when_a_base_domain_is_set(
        string baseDomain, string configured, string expected)
    {
        var services = new ServiceCollection();
        services.Configure<BaseDomainOptions>(options => options.BaseDomain = baseDomain);
        services.Configure<GoogleHealthPlatformOptions>(options => options.RedirectUri = configured);
        services.AddDataProtection();
        services.AddGoogleHealthPlatform();
        using var provider = services.BuildServiceProvider();

        Assert.Equal(expected, provider.GetRequiredService<IOptions<GoogleHealthPlatformOptions>>().Value.RedirectUri);
    }

    [Fact]
    public void Callback_hands_the_query_to_the_web_route_on_the_same_host()
    {
        var controller = new GoogleHealthOAuthCallbackController
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.QueryString = new QueryString("?code=abc&state=xyz");

        var result = Assert.IsType<StatusCodeResult>(controller.RelayGoogleHealthCallback());

        Assert.Equal(StatusCodes.Status303SeeOther, result.StatusCode);
        Assert.Equal("/settings/connectors/google-health/callback?code=abc&state=xyz",
            controller.Response.Headers.Location.ToString());
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }
}
