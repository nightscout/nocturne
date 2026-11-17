using Microsoft.AspNetCore.Mvc.Filters;

namespace Nocturne.API.Attributes;

/// <summary>
/// Lets a decorated action read the request body again after model binding has read it, for a
/// merge-patch whose bound type cannot tell an absent property from one set to its default.
/// </summary>
/// <remarks>
/// Buffering keeps Kestrel's request body limit: a larger body is refused as it is without it.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BufferRequestBodyAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context) =>
        context.HttpContext.Request.EnableBuffering();

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}
