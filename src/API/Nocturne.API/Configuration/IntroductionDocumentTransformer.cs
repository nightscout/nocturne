using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Nocturne.API.Configuration;

/// <summary>
/// Sets the OpenAPI <c>info.description</c>, which Scalar renders as the "Introduction" page,
/// from the document's <c>_introduction.md</c> (see <see cref="TagDescriptionLoader"/>).
/// Scalar lists the page's headings in the sidebar.
/// </summary>
public sealed class IntroductionDocumentTransformer : IOpenApiDocumentTransformer
{
    private readonly IReadOnlyDictionary<string, string> _introductions;

    public IntroductionDocumentTransformer(IWebHostEnvironment env)
    {
        _introductions = TagDescriptionLoader.LoadIntroductions(env);
    }

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (_introductions.TryGetValue(context.DocumentName, out var introduction))
            document.Info.Description = introduction;

        return Task.CompletedTask;
    }
}
