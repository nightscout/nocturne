using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Nocturne.Core.Models.Authorization;
// Microsoft.OpenApi also defines a Scope type; alias ours so the reference cannot drift.
using AuthScope = Nocturne.Core.Models.Authorization.Scope;

namespace Nocturne.API.OpenApi;

/// <summary>
/// Attaches per-operation security requirements to the runtime documents.
/// Nocturne endpoints get oauth2|bearer|instanceKey; Nightscout endpoints get apiSecret.
/// </summary>
public sealed class SecurityRequirementOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var actionDescriptor = context.Description.ActionDescriptor as ControllerActionDescriptor;
        if (actionDescriptor is null)
            return Task.CompletedTask;

        if (!SecuritySchemeDefinitions.RequiresAuthorization(
                actionDescriptor.MethodInfo, actionDescriptor.ControllerTypeInfo))
            return Task.CompletedTask;

        var document = context.Document;

        if (context.DocumentName == "nocturne")
        {
            // Each scheme is its own requirement entry → OR logic (any one suffices).
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SecuritySchemeDefinitions.OAuth2, document)] =
                    [AuthScope.FullAccess],
            });
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SecuritySchemeDefinitions.Bearer, document)] = [],
            });
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SecuritySchemeDefinitions.InstanceKey, document)] = [],
            });
        }
        else if (context.DocumentName == "nightscout")
        {
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SecuritySchemeDefinitions.ApiSecret, document)] = [],
            });
        }

        return Task.CompletedTask;
    }
}
