using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Nocturne.API.OpenApi;

/// <summary>
/// Microsoft OpenAPI transformer that mirrors <see cref="SummaryToDescriptionOperationProcessor"/>
/// for runtime Scalar docs. Moves the XML summary into description and derives a readable
/// title from the action method name.
/// </summary>
/// <remarks>
/// The XML <c>&lt;remarks&gt;</c> are dropped; see <see cref="SummaryToDescriptionOperationProcessor"/>.
/// </remarks>
public sealed class SummaryToDescriptionOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        operation.Description = SummaryToDescriptionOperationProcessor.PublishedDescription(operation.Summary);

        // Derive a readable title from the action method name.
        var methodName = (context.Description.ActionDescriptor as ControllerActionDescriptor)
            ?.MethodInfo.Name;

        if (methodName is null)
            return Task.CompletedTask;

        if (methodName.EndsWith("Async", StringComparison.Ordinal))
            methodName = methodName[..^"Async".Length];

        operation.Summary = SummaryToDescriptionOperationProcessor.Humanize(methodName);

        return Task.CompletedTask;
    }
}
