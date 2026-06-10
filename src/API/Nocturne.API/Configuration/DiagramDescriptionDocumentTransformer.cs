using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Nocturne.API.Configuration;

/// <summary>
/// Builds the OpenAPI info.description (the Scalar "Introduction" page). For the
/// Nocturne document this inlines the whole architecture manifest, each diagram's
/// mermaid source rendered as a fenced code block. The Nightscout document instead
/// gets a short intro that points readers at the per-collection Data Model pages, so
/// its introduction is not a single giant architecture dump. A lazy-loading renderer
/// registered via Scalar's <c>AddHeadContent</c> upgrades the blocks into rendered
/// diagrams when they scroll into view.
/// </summary>
public sealed class DiagramDescriptionDocumentTransformer : IOpenApiDocumentTransformer
{
    private const string NightscoutIntro = """
        The legacy Nightscout REST API (v1–v3), preserved for **1:1 compatibility** with the original JavaScript implementation.

        Every legacy write decomposes into Nocturne's granular v4 domain models, and every read projects back from v4 into the legacy shape — there are no standalone legacy tables. See the **Data Model** section for how each legacy collection (entries, treatments, devicestatus, profiles) maps to v4.
        """;

    private readonly string _nocturneDescription;

    public DiagramDescriptionDocumentTransformer(IWebHostEnvironment env)
    {
        _nocturneDescription = BuildDescription(env);
    }

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info.Description = context.DocumentName == "nightscout"
            ? NightscoutIntro
            : _nocturneDescription;
        return Task.CompletedTask;
    }

    private static string BuildDescription(IWebHostEnvironment env)
    {
        var diagramsDir = MermaidSourceLoader.ResolveDiagramsDir(env);
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
