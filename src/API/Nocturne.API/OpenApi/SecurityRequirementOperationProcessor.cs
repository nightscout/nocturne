using Nocturne.Core.Models.Authorization;
using NSwag;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;

namespace Nocturne.API.OpenApi;

/// <summary>
/// Attaches per-operation security requirements to the NSwag document, mirroring
/// <see cref="SecurityRequirementOperationTransformer"/> on the runtime documents.
/// </summary>
public sealed class SecurityRequirementOperationProcessor : IOperationProcessor
{
    public bool Process(OperationProcessorContext context)
    {
        if (!SecuritySchemeDefinitions.RequiresAuthorization(context.MethodInfo, context.ControllerType))
            return true;

        var operation = context.OperationDescription.Operation;
        operation.Security ??= [];

        // Each scheme is its own requirement entry → OR logic (any one suffices).
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [SecuritySchemeDefinitions.OAuth2] = new[] { OAuthScopes.FullAccess },
        });
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [SecuritySchemeDefinitions.Bearer] = [],
        });
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [SecuritySchemeDefinitions.InstanceKey] = [],
        });

        return true;
    }
}
