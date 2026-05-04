using Microsoft.AspNetCore.Mvc;
using NJsonSchema;
using NSwag;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;

namespace Nocturne.API.OpenApi;

/// <summary>
/// NSwag always emits <c>multipart/form-data</c> for <c>[FromForm]</c> parameters,
/// ignoring the <c>[Consumes]</c> attribute. This processor reads <c>[Consumes]</c>
/// and replaces the request body content type when it specifies
/// <c>application/x-www-form-urlencoded</c> without <c>multipart/form-data</c>.
/// </summary>
public sealed class ConsumesContentTypeOperationProcessor : IOperationProcessor
{
    public bool Process(OperationProcessorContext context)
    {
        var consumesAttr = context.MethodInfo
            .GetCustomAttributes(typeof(ConsumesAttribute), inherit: true)
            .OfType<ConsumesAttribute>()
            .FirstOrDefault();

        if (consumesAttr is null)
            return true;

        var contentTypes = consumesAttr.ContentTypes;
        var hasUrlEncoded = contentTypes.Contains("application/x-www-form-urlencoded");
        var hasMultipart = contentTypes.Contains("multipart/form-data");

        // Only rewrite when the endpoint explicitly wants url-encoded and NOT multipart.
        if (!hasUrlEncoded || hasMultipart)
            return true;

        var body = context.OperationDescription.Operation.RequestBody;
        if (body?.Content == null || !body.Content.ContainsKey("multipart/form-data"))
            return true;

        var multipartMedia = body.Content["multipart/form-data"];
        body.Content.Remove("multipart/form-data");
        body.Content["application/x-www-form-urlencoded"] = multipartMedia;

        return true;
    }
}
