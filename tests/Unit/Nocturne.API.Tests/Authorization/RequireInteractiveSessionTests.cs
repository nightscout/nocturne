using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Controllers.V4.Platform;
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

        { typeof(EmailConfirmationController), nameof(EmailConfirmationController.Send) },

        { typeof(TrustedDevicesController), nameof(TrustedDevicesController.List) },
        { typeof(TrustedDevicesController), nameof(TrustedDevicesController.Revoke) },
        { typeof(TrustedDevicesController), nameof(TrustedDevicesController.RevokeAll) },

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

        { typeof(ChatIdentityController), nameof(ChatIdentityController.ClaimLink) },
        { typeof(CustomReportsController), nameof(CustomReportsController.CreateCustomReport) },
        { typeof(CustomReportsController), nameof(CustomReportsController.UpdateCustomReport) },
        { typeof(CustomReportsController), nameof(CustomReportsController.DeleteCustomReport) },
        { typeof(ServicesController), nameof(ServicesController.GetConnections) },
        { typeof(GuestLinkController), nameof(GuestLinkController.CreateGuestLink) },
        { typeof(ShareLinkController), nameof(ShareLinkController.RevealShareLink) },
        { typeof(ShareLinkController), nameof(ShareLinkController.RotateShareLink) },
        { typeof(ShareLinkController), nameof(ShareLinkController.SetShareLinkScopes) },
        { typeof(ShareLinkController), nameof(ShareLinkController.SetShareLinkFullHistory) },
        { typeof(MemberInviteController), nameof(MemberInviteController.CreateInvite) },
        { typeof(MemberInviteController), nameof(MemberInviteController.StepDown) },
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

    [Fact]
    public void EveryActionCarryingTheAttribute_IsListed()
    {
        var carrying = typeof(RequireInteractiveSessionAttribute).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
                .Where(m => m.GetCustomAttribute<RequireInteractiveSessionAttribute>() is not null
                    || t.GetCustomAttribute<RequireInteractiveSessionAttribute>() is not null)
                .Select(m => (t, m.Name)))
            .ToHashSet();

        var listed = GatedEndpoints.Select(row => ((Type)row[0], (string)row[1])).ToHashSet();

        carrying.Should().BeEquivalentTo(listed);
    }

    /// <summary>
    /// Every credential type, and whether the filter admits it: only the ones a person's own sign-in
    /// produces are admitted. <see cref="AuthType.LegacyJwt"/> is a session token presented as a
    /// bearer; a JWT exchanged from an access token carries its grant and resolves as
    /// <see cref="AuthType.OAuthAccessToken"/>. <see cref="AuthType.OidcToken"/> is a provider's ID
    /// token sent as a bearer, which skips this instance's second factor.
    /// </summary>
    private static readonly IReadOnlyDictionary<AuthType, bool> AdmittedByCredentialType =
        new Dictionary<AuthType, bool>
        {
            [AuthType.None] = false,
            [AuthType.SessionCookie] = true,
            [AuthType.LegacyJwt] = true,
            [AuthType.OidcToken] = false,
            [AuthType.ApiKey] = false,
            [AuthType.DirectGrant] = false,
            [AuthType.OAuthAccessToken] = false,
            [AuthType.Guest] = false,
            [AuthType.InstanceKey] = false,
            [AuthType.PlatformAccess] = false,
        };

    public static TheoryData<AuthType> CredentialTypes => new(Enum.GetValues<AuthType>());

    [Fact]
    public void EveryCredentialType_IsClassified() =>
        AdmittedByCredentialType.Keys.Should().BeEquivalentTo(Enum.GetValues<AuthType>());

    [Theory]
    [MemberData(nameof(CredentialTypes))]
    public void OnlyASignInCredential_IsAdmitted(AuthType authType)
    {
        var result = Run(new AuthContext
        {
            IsAuthenticated = true, AuthType = authType, SubjectId = Guid.CreateVersion7(),
        });

        if (AdmittedByCredentialType.GetValueOrDefault(authType))
        {
            result.Should().BeNull();
        }
        else
        {
            result.Should().BeOfType<ForbidResult>();
        }
    }

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
