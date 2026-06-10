using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Authorization;
using Nocturne.API.Middleware.Handlers;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Middleware.Handlers;

/// <summary>
/// Tests for <see cref="InstanceKeyHandler"/>.
///
/// Security regression context: SvelteKit historically forwarded the
/// X-Instance-Key header on every request — including anonymous browser
/// requests to private tenants. Because the instance key grants full admin,
/// that bypassed the per-tenant public-access toggle entirely. The handler
/// now requires an explicit X-Instance-Service marker so that a bare instance
/// key (e.g. one accidentally forwarded onto a user request) does NOT
/// authenticate; such requests fall through to public-access resolution.
/// </summary>
[Trait("Category", "Unit")]
public class InstanceKeyHandlerTests
{
    private const string PlainKey = "test-instance-key";

    private static InstanceKeyHandler CreateHandler()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ServiceNames.ConfigKeys.InstanceKey] = PlainKey,
            })
            .Build();
        return new InstanceKeyHandler(new InstanceKeyValidator(config), NullLogger<InstanceKeyHandler>.Instance);
    }

    private static string ValidHash => HashUtils.Sha256Hex(PlainKey);

    [Fact]
    public async Task ValidInstanceKey_WithoutServiceMarker_Skips()
    {
        var handler = CreateHandler();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Instance-Key"] = ValidHash;
        // No X-Instance-Service marker — this is what an anonymous browser
        // request looks like, and it must NOT authenticate as admin.

        var result = await handler.AuthenticateAsync(context);

        Assert.True(result.ShouldSkip,
            "a bare instance key without a service marker must skip so the " +
            "request falls through to public-access resolution");
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ValidInstanceKey_WithServiceMarker_AuthenticatesAsAdmin()
    {
        var handler = CreateHandler();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Instance-Key"] = ValidHash;
        context.Request.Headers["X-Instance-Service"] = "nocturne-web";

        var result = await handler.AuthenticateAsync(context);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.AuthContext);
        Assert.Equal(AuthType.InstanceKey, result.AuthContext!.AuthType);
        Assert.Contains("*", result.AuthContext.Permissions);
        Assert.True(result.AuthContext.IsPlatformAdmin);
    }

    [Fact]
    public async Task NoInstanceKey_Skips()
    {
        var handler = CreateHandler();
        var context = new DefaultHttpContext();
        // Even a service marker alone is not a credential.
        context.Request.Headers["X-Instance-Service"] = "nocturne-web";

        var result = await handler.AuthenticateAsync(context);

        Assert.True(result.ShouldSkip);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task InvalidInstanceKey_WithServiceMarker_Fails()
    {
        var handler = CreateHandler();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Instance-Key"] = "deadbeef";
        context.Request.Headers["X-Instance-Service"] = "nocturne-web";

        var result = await handler.AuthenticateAsync(context);

        Assert.False(result.Succeeded);
        Assert.False(result.ShouldSkip);
        Assert.NotNull(result.Error);
    }
}
