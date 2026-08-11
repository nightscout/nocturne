using NSwag.Generation;

namespace Nocturne.API.OpenApi;

/// <summary>
/// Adds the security scheme and requirement processors to an NSwag document. The build-time
/// document is configured at two sites — the application host and <c>NSwagStartup</c> — so both
/// register through here.
/// </summary>
public static class NSwagSecurityRegistration
{
    public static void AddNocturneSecurity(this OpenApiDocumentGeneratorSettings settings)
    {
        settings.DocumentProcessors.Add(new SecuritySchemeDocumentProcessor());
        settings.OperationProcessors.Add(new SecurityRequirementOperationProcessor());
    }
}
