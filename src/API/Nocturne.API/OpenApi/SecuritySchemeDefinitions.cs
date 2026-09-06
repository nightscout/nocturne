using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.OpenApi;

/// <summary>
/// The security schemes the API publishes, and the rule for which operations require one.
/// </summary>
/// <remarks>
/// Two OpenAPI pipelines emit these: Microsoft.OpenApi serves the runtime documents for Scalar,
/// NSwag emits the build-time document that feeds the TypeScript client and the published SDK
/// specs. The two have separate object models, so each pipeline builds its own scheme objects
/// from the values here.
/// </remarks>
public static class SecuritySchemeDefinitions
{
    public const string OAuth2 = "oauth2";
    public const string Bearer = "bearer";
    public const string InstanceKey = "instanceKey";
    public const string ApiSecret = "apiSecret";

    public const string AuthorizationUrl = "/api/oauth/authorize";
    public const string TokenUrl = "/api/oauth/token";

    public const string InstanceKeyHeader = "X-Instance-Key";
    public const string ApiSecretHeader = "api-secret";

    public const string BearerFormat = "JWT or noc_* direct grant token";

    public const string OAuth2Description =
        "OAuth 2.0 Authorization Code with PKCE. "
        + "All clients are public — PKCE is mandatory, no client secrets.";

    public const string BearerDescription =
        "Paste an existing token: an OAuth access token (JWT), "
        + "OIDC token, or a direct grant token (prefixed `noc_`).";

    public const string InstanceKeyDescription =
        "Platform-internal instance key. Grants full admin permissions "
        + "— intended for infrastructure services, not end users.";

    public const string ApiSecretDescription =
        "Nightscout API secret (SHA-1 hash). "
        + "Grants full read/write access to the tenant.";

    public static readonly IReadOnlyDictionary<string, string> OAuth2Scopes =
        new Dictionary<string, string>
        {
            [Scope.FullAccess] = "Full access (read, write, delete)",
            [Scope.HealthRead] =
                "Read all health data (glucose, treatments, devices, therapy settings)",
            [Scope.GlucoseRead] = "Read glucose data",
            [Scope.GlucoseReadWrite] = "Read and write glucose data",
            [Scope.TreatmentsRead] = "Read treatments",
            [Scope.TreatmentsReadWrite] = "Read and write treatments",
            [Scope.DevicesRead] = "Read device status data",
            [Scope.DevicesReadWrite] = "Read and write device status data",
            [Scope.TherapyRead] = "Read therapy settings",
            [Scope.TherapyReadWrite] = "Read and write therapy settings",
            [Scope.AlertsRead] = "Read alert configuration",
            [Scope.AlertsReadWrite] = "Read and write alert configuration",
            [Scope.ReportsRead] = "Read reports",
            [Scope.IdentityRead] = "Read identity information",
            [Scope.SharingReadWrite] = "Manage sharing settings",
        };

    /// <summary>
    /// Whether an operation needs credentials. Authorization is default-deny — the
    /// <see cref="Microsoft.AspNetCore.Authorization.AuthorizationOptions.FallbackPolicy"/> covers
    /// every endpoint that carries no authorization attribute — so an operation is callable
    /// anonymously only where it opts out with <c>[AllowAnonymous]</c>.
    /// </summary>
    public static bool RequiresAuthorization(MethodInfo method, Type controllerType) =>
        method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).Length == 0
        && controllerType.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).Length == 0;
}
