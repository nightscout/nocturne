using NSwag.Generation.AspNetCore;
using OpenApi.Remote.Processors;

namespace Nocturne.API.OpenApi;

/// <summary>
/// The build-time "nocturne" NSwag document, which feeds the TypeScript client and the published
/// SDK specs. NSwag boots the app through <c>NSwagStartup</c> rather than the application host, so
/// this is the only configuration site the generated spec ever sees.
/// </summary>
public static class NSwagDocumentConfiguration
{
    public static void Configure(AspNetCoreOpenApiDocumentGeneratorSettings settings)
    {
        settings.DocumentName = "nocturne";
        settings.Title = "Nocturne API";
        settings.Version = "0.0.1";

        settings.AddOperationFilter(context =>
            ApiDocumentMembership.InNocturneDocument(context.ControllerType.Namespace ?? string.Empty));

        settings.OperationProcessors.Add(new RemoteFunctionOperationProcessor());
        settings.OperationProcessors.Add(new ConsumesContentTypeOperationProcessor());
        settings.OperationProcessors.Add(new ControllerNameTagOperationProcessor());
        settings.OperationProcessors.Add(new SummaryToDescriptionOperationProcessor());
        settings.DocumentProcessors.Add(new SecuritySchemeDocumentProcessor());
        settings.OperationProcessors.Add(new SecurityRequirementOperationProcessor());
    }
}
