using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

[Trait("Category", "Unit")]
public class DenyNoAccountViewerAttributeTests
{
    [Fact]
    public void APublicShare_IsRefused()
    {
        var context = Context(new AuthContext { IsAuthenticated = false });
        context.SetShareAccess();

        DenyNoAccountViewerAttribute.Refuses(context).Should().BeTrue();
    }

    [Fact]
    public void AGuest_IsRefused()
    {
        var context = Context(new AuthContext { IsAuthenticated = true, AuthType = AuthType.Guest });

        DenyNoAccountViewerAttribute.Refuses(context).Should().BeTrue();
    }

    [Theory]
    [InlineData(AuthType.SessionCookie)]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.DirectGrant)]
    public void AMember_IsAdmitted(AuthType authType)
    {
        DenyNoAccountViewerAttribute.Refuses(
                Context(new AuthContext { IsAuthenticated = true, AuthType = authType }))
            .Should().BeFalse();
    }

    private static HttpContext Context(AuthContext auth)
    {
        var context = new DefaultHttpContext();
        context.SetAuthContext(auth);
        return context;
    }
}
