using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Nocturne.API.Configuration;

/// <summary>
/// Builds the OpenAPI info.description from the diagram manifest, inlining
/// each diagram's mermaid source as a fenced code block. A lazy-loading
/// renderer registered via Scalar's <c>AddHeadContent</c> upgrades the
/// blocks into rendered diagrams when they scroll into view.
/// </summary>
public sealed class DiagramDescriptionDocumentTransformer : IOpenApiDocumentTransformer
{
    private readonly string _description;

    public DiagramDescriptionDocumentTransformer(IWebHostEnvironment env)
    {
        _description = BuildDescription(env);
    }

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info.Description = _description;
        return Task.CompletedTask;
    }

    private static string BuildDescription(IWebHostEnvironment env)
    {
        var diagramsDir = Path.Combine(env.ContentRootPath, "..", "..", "..", "docs", "diagrams");
        var manifestPath = Path.Combine(diagramsDir, "diagrams.yaml");

        if (!File.Exists(manifestPath))
        {
            return "Modern diabetes management API. For support, join our Discord.";
        }

        var yaml = File.ReadAllText(manifestPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        var manifest = deserializer.Deserialize<DiagramManifest>(yaml);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Modern diabetes management API. For support, join our Discord.");
        sb.AppendLine();
        sb.AppendLine("## Architecture");
        sb.AppendLine();

        foreach (var diagram in manifest.Diagrams)
        {
            var mermaid = MermaidSourceLoader.TryRead(diagramsDir, diagram.Source);
            if (mermaid is null) continue;

            sb.AppendLine($"### {diagram.Title}");
            if (!string.IsNullOrWhiteSpace(diagram.Description))
            {
                sb.AppendLine(diagram.Description);
            }
            sb.AppendLine();
            sb.AppendLine("```mermaid");
            sb.AppendLine(mermaid);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private sealed class DiagramManifest
    {
        public List<DiagramEntry> Diagrams { get; set; } = [];
    }

    private sealed class DiagramEntry
    {
        public string Source { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public string? Auto { get; set; }
        public string? Module { get; set; }
        public List<string>? Tags { get; set; }
    }
}
