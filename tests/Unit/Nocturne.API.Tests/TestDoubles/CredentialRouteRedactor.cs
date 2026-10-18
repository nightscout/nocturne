using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V2;
using Nocturne.API.Controllers.V4.Monitoring;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>
/// A <see cref="CredentialPathRedactor"/> loaded with two real credential actions, for tests of a
/// sink that runs without the full endpoint table.
/// </summary>
internal static class CredentialRouteRedactor
{
    public const string AlertInvitePath = "/api/v4/alert-invites/{token}";

    public const string TokenExchangePath = "/api/v2/authorization/request/{accessToken}";

    public static CredentialPathRedactor Create()
    {
        var redactor = new CredentialPathRedactor();
        redactor.Load(
        [
            Endpoint(AlertInvitePath, typeof(AlertInvitesController), nameof(AlertInvitesController.ValidateInvite)),
            Endpoint(
                TokenExchangePath,
                typeof(AuthorizationController),
                nameof(AuthorizationController.GenerateJwtFromAccessToken)
            ),
        ]);
        return redactor;
    }

    private static RouteEndpoint Endpoint(string template, Type controller, string action) =>
        new(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(template),
            order: 0,
            new EndpointMetadataCollection(
                new ControllerActionDescriptor { MethodInfo = controller.GetMethod(action)! }
            ),
            displayName: template
        );
}
