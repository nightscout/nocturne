using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Nocturne.API.OpenApi;

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
            document.Components.SecuritySchemes[SecuritySchemeDefinitions.OAuth2] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.OAuth2,
                Description = SecuritySchemeDefinitions.OAuth2Description,
                Flows = new OpenApiOAuthFlows
                {
                    AuthorizationCode = new OpenApiOAuthFlow
                    {
                        AuthorizationUrl = new Uri(SecuritySchemeDefinitions.AuthorizationUrl, UriKind.Relative),
                        TokenUrl = new Uri(SecuritySchemeDefinitions.TokenUrl, UriKind.Relative),
                        Scopes = new Dictionary<string, string>(SecuritySchemeDefinitions.OAuth2Scopes),
                    },
                },
            };

            document.Components.SecuritySchemes[SecuritySchemeDefinitions.Bearer] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = SecuritySchemeDefinitions.BearerFormat,
                Description = SecuritySchemeDefinitions.BearerDescription,
            };

            document.Components.SecuritySchemes[SecuritySchemeDefinitions.InstanceKey] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                Name = SecuritySchemeDefinitions.InstanceKeyHeader,
                In = ParameterLocation.Header,
                Description = SecuritySchemeDefinitions.InstanceKeyDescription,
            };
        }
        else if (context.DocumentName == "nightscout")
        {
            document.Components.SecuritySchemes[SecuritySchemeDefinitions.ApiSecret] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                Name = SecuritySchemeDefinitions.ApiSecretHeader,
                In = ParameterLocation.Header,
                Description = SecuritySchemeDefinitions.ApiSecretDescription,
            };
        }

        return Task.CompletedTask;
    }
}
