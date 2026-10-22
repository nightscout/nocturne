using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Pins which endpoints only the account holder's own sign-in may call, and what the filter admits.
/// </summary>
/// <remarks>
/// A reflection test for the same reason as <see cref="DemoSubjectGatedEndpointsTests"/>: the
/// failure being guarded is the attribute going missing from one action.
/// </remarks>
[Trait("Category", "Unit")]
public class RequireInteractiveSessionTests
{
    public static TheoryData<Type, string> GatedEndpoints => new()
    {
        { typeof(DirectGrantController), nameof(DirectGrantController.Create) },
        { typeof(DirectGrantController), nameof(DirectGrantController.List) },
        { typeof(DirectGrantController), nameof(DirectGrantController.Revoke) },

        { typeof(OAuthController), nameof(OAuthController.ApproveConsent) },
        { typeof(OAuthController), nameof(OAuthController.DeviceApprove) },
        { typeof(OAuthController), nameof(OAuthController.GetGrants) },
        { typeof(OAuthController), nameof(OAuthController.DeleteGrant) },
        { typeof(OAuthController), nameof(OAuthController.UpdateGrant) },

        { typeof(PasskeyController), nameof(PasskeyController.RegisterOptions) },
        { typeof(PasskeyController), nameof(PasskeyController.RegisterComplete) },
        { typeof(PasskeyController), nameof(PasskeyController.RemoveCredential) },
        { typeof(PasskeyController), nameof(PasskeyController.RegenerateRecoveryCodes) },

        { typeof(TotpController), nameof(TotpController.Setup) },
        { typeof(TotpController), nameof(TotpController.VerifySetup) },
        { typeof(TotpController), nameof(TotpController.RemoveCredential) },

        { typeof(OidcController), nameof(OidcController.Link) },
        { typeof(OidcController), nameof(OidcController.LinkCallback) },
        { typeof(OidcController), nameof(OidcController.UnlinkIdentity) },

        { typeof(GuestLinkController), nameof(GuestLinkController.CreateGuestLink) },
        { typeof(ShareLinkController), nameof(ShareLinkController.RevealShareLink) },
        { typeof(ShareLinkController), nameof(ShareLinkController.RotateShareLink) },
        { typeof(ShareLinkController), nameof(ShareLinkController.SetShareLinkScopes) },
        { typeof(ShareLinkController), nameof(ShareLinkController.SetShareLinkFullHistory) },
        { typeof(MemberInviteController), nameof(MemberInviteController.CreateInvite) },
        { typeof(MemberRecoveryController), nameof(MemberRecoveryController.IssueRecoveryCode) },
    };

    [Theory]
    [MemberData(nameof(GatedEndpoints))]
    public void Endpoint_RequiresAnInteractiveSession(Type controller, string action)
    {
        var method = controller.GetMethod(action, BindingFlags.Public | BindingFlags.Instance)!;

        (method.GetCustomAttribute<RequireInteractiveSessionAttribute>() is not null
            || controller.GetCustomAttribute<RequireInteractiveSessionAttribute>() is not null)
            .Should().BeTrue($"{controller.Name}.{action} manages credentials or sign-in factors");
    }

    [Theory]
    [InlineData(AuthType.SessionCookie)]
    [InlineData(AuthType.LegacyJwt)]
    [InlineData(AuthType.OidcToken)]
    public void InteractiveCredential_IsAdmitted(AuthType authType) =>
        Run(new AuthContext { IsAuthenticated = true, AuthType = authType, SubjectId = Guid.CreateVersion7() })
            .Should().BeNull();

    [Theory]
    [InlineData(AuthType.ApiKey)]
    [InlineData(AuthType.DirectGrant)]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.Guest)]
    [InlineData(AuthType.InstanceKey)]
    [InlineData(AuthType.PlatformAccess)]
    public void DelegatedCredential_IsForbidden(AuthType authType) =>
        Run(new AuthContext { IsAuthenticated = true, AuthType = authType, SubjectId = Guid.CreateVersion7() })
            .Should().BeOfType<ForbidResult>();

    /// <summary>
    /// Left to the action: the recovery session a spent recovery code mints is not an
    /// authenticated credential, and the registration endpoints must still admit it.
    /// </summary>
    [Fact]
    public void UnauthenticatedRequest_IsLeftToTheAction() =>
        Run(new AuthContext { IsAuthenticated = false }).Should().BeNull();

    private static IActionResult? Run(AuthContext auth)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = auth;

        var context = new AuthorizationFilterContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            []);

        new RequireInteractiveSessionAttribute().OnAuthorization(context);
        return context.Result;
    }
}
