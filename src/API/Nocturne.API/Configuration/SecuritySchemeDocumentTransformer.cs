using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Nocturne.API.Configuration;

/// <summary>
/// Registers OpenAPI security schemes in components/securitySchemes.
/// Nocturne document gets oauth2 + bearer + instanceKey.
/// Nightscout document gets apiSecret.
/// </summary>
public sealed class SecuritySchemeDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        if (context.DocumentName == "nocturne")
        {
            document.Components.SecuritySchemes["oauth2"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.OAuth2,
                Description =
                    "OAuth 2.0 Authorization Code with PKCE. "
                    + "All clients are public — PKCE is mandatory, no client secrets.",
                Flows = new OpenApiOAuthFlows
                {
                    AuthorizationCode = new OpenApiOAuthFlow
                    {
                        AuthorizationUrl = new Uri("/api/oauth/authorize", UriKind.Relative),
                        TokenUrl = new Uri("/api/oauth/token", UriKind.Relative),
                        Scopes = new Dictionary<string, string>
                        {
                            ["*"] = "Full access (read, write, delete)",
                            ["health.read"] =
                                "Read all health data (glucose, treatments, devices, therapy settings)",
                            ["glucose.read"] = "Read glucose data",
                            ["glucose.readwrite"] = "Read and write glucose data",
                            ["treatments.read"] = "Read treatments",
                            ["treatments.readwrite"] = "Read and write treatments",
                            ["devices.read"] = "Read device status data",
                            ["devices.readwrite"] = "Read and write device status data",
                            ["therapy.read"] = "Read therapy settings",
                            ["therapy.readwrite"] = "Read and write therapy settings",
                            ["alerts.read"] = "Read alert configuration",
                            ["alerts.readwrite"] = "Read and write alert configuration",
                            ["reports.read"] = "Read reports",
                            ["identity.read"] = "Read identity information",
                            ["sharing.readwrite"] = "Manage sharing settings",
                        },
                    },
                },
            };

            document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT or noc_* direct grant token",
                Description =
                    "Paste an existing token: an OAuth access token (JWT), "
                    + "OIDC token, or a direct grant token (prefixed `noc_`).",
            };

            document.Components.SecuritySchemes["instanceKey"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                Name = "X-Instance-Key",
                In = ParameterLocation.Header,
                Description =
                    "Platform-internal instance key. Grants full admin permissions "
                    + "— intended for infrastructure services, not end users.",
            };
        }
        else if (context.DocumentName == "nightscout")
        {
            document.Components.SecuritySchemes["apiSecret"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                Name = "api-secret",
                In = ParameterLocation.Header,
                Description =
                    "Nightscout API secret (SHA-1 hash). "
                    + "Grants full read/write access to the tenant.",
            };
        }

        return Task.CompletedTask;
    }
}
