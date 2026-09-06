using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Nocturne.API.OpenApi;

/// <summary>
/// Decides which controllers belong to which published OpenAPI document, by controller
/// namespace. The build-time NSwag document (TypeScript client and SDK specs) and the runtime
/// Microsoft.OpenApi documents (Scalar docs) both ask here, so they cannot disagree about an
/// endpoint.
/// </summary>
public static class ApiDocumentMembership
{
    /// <summary>The "nocturne" document: the V4 API, authentication, and the root controllers.</summary>
    public static bool InNocturneDocument(string controllerNamespace) =>
        InVersion(controllerNamespace, "V4")
        || controllerNamespace.Contains(".Controllers.Authentication", StringComparison.Ordinal)
        || controllerNamespace == "Nocturne.API.Controllers";

    public static bool InNocturneDocument(ApiDescription description) =>
        InNocturneDocument(ControllerNamespace(description));

    /// <summary>The "nightscout" document: the V1–V3 Nightscout compatibility surface.</summary>
    public static bool InNightscoutDocument(ApiDescription description)
    {
        var controllerNamespace = ControllerNamespace(description);
        return InVersion(controllerNamespace, "V1")
            || InVersion(controllerNamespace, "V2")
            || InVersion(controllerNamespace, "V3");
    }

    private static bool InVersion(string controllerNamespace, string version) =>
        controllerNamespace.Contains($".Controllers.{version}.", StringComparison.Ordinal)
        || controllerNamespace.EndsWith($".Controllers.{version}", StringComparison.Ordinal);

    private static string ControllerNamespace(ApiDescription description) =>
        (description.ActionDescriptor as ControllerActionDescriptor)?.ControllerTypeInfo.Namespace
        ?? string.Empty;
}
