using NSwag;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;

namespace Nocturne.API.OpenApi;

/// <summary>
/// Registers <see cref="SecuritySchemeDefinitions"/> in <c>components/securitySchemes</c> of the
/// NSwag document. Without them the published SDKs generate no authentication plumbing.
/// </summary>
public sealed class SecuritySchemeDocumentProcessor : IDocumentProcessor
{
    public void Process(DocumentProcessorContext context)
    {
        var schemes = context.Document.Components.SecuritySchemes;

        schemes[SecuritySchemeDefinitions.OAuth2] = new OpenApiSecurityScheme
        {
            Type = OpenApiSecuritySchemeType.OAuth2,
            Description = SecuritySchemeDefinitions.OAuth2Description,
            Flows = new OpenApiOAuthFlows
            {
                AuthorizationCode = new OpenApiOAuthFlow
                {
                    AuthorizationUrl = SecuritySchemeDefinitions.AuthorizationUrl,
                    TokenUrl = SecuritySchemeDefinitions.TokenUrl,
                    Scopes = new Dictionary<string, string>(SecuritySchemeDefinitions.OAuth2Scopes),
                },
            },
        };

        schemes[SecuritySchemeDefinitions.Bearer] = new OpenApiSecurityScheme
        {
            Type = OpenApiSecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = SecuritySchemeDefinitions.BearerFormat,
            Description = SecuritySchemeDefinitions.BearerDescription,
        };

        schemes[SecuritySchemeDefinitions.InstanceKey] = new OpenApiSecurityScheme
        {
            Type = OpenApiSecuritySchemeType.ApiKey,
            Name = SecuritySchemeDefinitions.InstanceKeyHeader,
            In = OpenApiSecurityApiKeyLocation.Header,
            Description = SecuritySchemeDefinitions.InstanceKeyDescription,
        };
    }
}
