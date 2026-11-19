using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Nocturne.API.OpenApi;

/// <summary>
/// Tags each operation with its controller, so the Scalar sidebar lists one entry for each
/// controller. The <c>[Tags]</c> area becomes the group; see <see cref="ControllerDocs"/>.
/// </summary>
public sealed class ControllerTagOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (ControllerDocs.For(context.Description) is { } entry && context.Document is { } document)
        {
            // A reference resolves only against a tag the document declares.
            document.Tags ??= new HashSet<OpenApiTag>();
            if (document.Tags.All(tag => tag.Name != entry.Tag))
                document.Tags.Add(new OpenApiTag { Name = entry.Tag });

            operation.Tags = new HashSet<OpenApiTagReference> { new(entry.Tag, document) };
        }

        return Task.CompletedTask;
    }
}
