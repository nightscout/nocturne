using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Nocturne.API.Configuration;

/// <summary>
/// Adds human-readable descriptions to OpenAPI tags so Scalar displays an overview
/// for each controller group. Descriptions use GitHub-flavored markdown and are
/// authored as folder-based markdown files under <c>docs/api-descriptions</c>
/// (loaded by <see cref="TagDescriptionLoader"/>). Embeds ER diagrams from the diagram
/// manifest into matching tag descriptions.
/// </summary>
public sealed class TagDescriptionDocumentTransformer : IOpenApiDocumentTransformer
{
    private readonly Dictionary<string, List<DiagramRef>> _tagDiagrams;

    /// <summary>Tag name → markdown description, loaded from <c>docs/api-descriptions</c>.</summary>
    private readonly Dictionary<string, string> _descriptions;

    /// <summary>Tag name → sidebar label override (<c>x-displayName</c>).</summary>
    private readonly Dictionary<string, string> _displayNames;

    /// <summary>
    /// Conceptual guide tags that have no operations of their own but still render as a
    /// standalone sidebar page from their description (and any diagrams mapped to them),
    /// keyed by the OpenAPI document they belong to. The Nocturne document carries the
    /// <c>Syncing</c> guide; the Nightscout document carries the legacy-to-v4 model-mapping
    /// pages, one per legacy collection. Both are marked <c>standalone: true</c> in their
    /// markdown frontmatter.
    /// </summary>
    private readonly Dictionary<string, string[]> _standaloneDocTagsByDocument;

    public TagDescriptionDocumentTransformer(IWebHostEnvironment env)
    {
        _tagDiagrams = BuildTagDiagramMap(env);

        var loaded = TagDescriptionLoader.Load(env);
        _descriptions = loaded.ToDictionary(d => d.TagName, d => d.Markdown, StringComparer.Ordinal);
        _displayNames = loaded
            .Where(d => d.DisplayName is not null)
            .ToDictionary(d => d.TagName, d => d.DisplayName!, StringComparer.Ordinal);
        _standaloneDocTagsByDocument = loaded
            .Where(d => d.Standalone)
            .GroupBy(d => d.Document, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(d => d.TagName).ToArray(), StringComparer.Ordinal);
    }

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        // Collect every tag name already referenced by operations.
        var usedTags = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pathItem in document.Paths.Values)
        {
            if (pathItem.Operations is null) continue;
            foreach (var operation in pathItem.Operations.Values)
            {
                if (operation.Tags is null) continue;
                foreach (var tag in operation.Tags)
                {
                    if (tag is IOpenApiTag openApiTag && openApiTag.Name is not null)
                        usedTags.Add(openApiTag.Name);
                }
            }
        }

        // Standalone conceptual guide pages have no operations but still render as
        // their own sidebar entry. Surface only the ones belonging to this document.
        if (_standaloneDocTagsByDocument.TryGetValue(context.DocumentName, out var standaloneTags))
        {
            foreach (var docTag in standaloneTags)
                usedTags.Add(docTag);
        }

        // Build the document-level tag set with descriptions.
        var tags = new HashSet<OpenApiTag>(TagNameComparer.Instance);

        foreach (var tagName in usedTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            _descriptions.TryGetValue(tagName, out var description);

            // Append any ER diagrams mapped to this tag.
            if (_tagDiagrams.TryGetValue(tagName, out var diagrams))
            {
                var sb = new System.Text.StringBuilder();
                if (!string.IsNullOrWhiteSpace(description))
                {
                    sb.AppendLine(description);
                    sb.AppendLine();
                }

                sb.AppendLine("## Data Model");
                sb.AppendLine();

                foreach (var diagram in diagrams)
                {
                    sb.AppendLine($"**{diagram.Title}**");
                    if (!string.IsNullOrWhiteSpace(diagram.Description))
                        sb.AppendLine($"_{diagram.Description}_");
                    sb.AppendLine();
                    sb.AppendLine("```mermaid");
                    sb.AppendLine(diagram.MermaidSource);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }

                description = sb.ToString().TrimEnd();
            }

            var tagObj = new OpenApiTag
            {
                Name = tagName,
                Description = description,
            };

            if (_displayNames.TryGetValue(tagName, out var displayName))
            {
                tagObj.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                tagObj.Extensions["x-displayName"] = new JsonNodeExtension(JsonValue.Create(displayName));
            }

            tags.Add(tagObj);
        }

        document.Tags = tags;

        return Task.CompletedTask;
    }

    private static Dictionary<string, List<DiagramRef>> BuildTagDiagramMap(IWebHostEnvironment env)
    {
        var diagramsDir = MermaidSourceLoader.ResolveDiagramsDir(env);
        var manifestPath = Path.Combine(diagramsDir, "diagrams.yaml");
        var map = new Dictionary<string, List<DiagramRef>>(StringComparer.Ordinal);

        if (!File.Exists(manifestPath))
            return map;

        var yaml = File.ReadAllText(manifestPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        var manifest = deserializer.Deserialize<DiagramManifest>(yaml);

        foreach (var diagram in manifest.Diagrams)
        {
            if (diagram.Tags is not { Count: > 0 })
                continue;

            var mermaid = MermaidSourceLoader.TryRead(diagramsDir, diagram.Source);
            if (mermaid is null) continue;

            var diagramRef = new DiagramRef(diagram.Title, diagram.Description, mermaid);

            foreach (var tag in diagram.Tags)
            {
                if (!map.TryGetValue(tag, out var list))
                {
                    list = [];
                    map[tag] = list;
                }
                list.Add(diagramRef);
            }
        }

        return map;
    }

    private sealed record DiagramRef(string Title, string? Description, string MermaidSource);

    private sealed class DiagramManifest
    {
        public List<DiagramEntry> Diagrams { get; set; } = [];
    }

    private sealed class DiagramEntry
    {
        public string Source { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public List<string>? Tags { get; set; }
        public string? Auto { get; set; }
        public string? Module { get; set; }
    }

    private sealed class TagNameComparer : IEqualityComparer<OpenApiTag>
    {
        public static readonly TagNameComparer Instance = new();

        public bool Equals(OpenApiTag? x, OpenApiTag? y) =>
            string.Equals(x?.Name, y?.Name, StringComparison.Ordinal);

        public int GetHashCode(OpenApiTag obj) =>
            obj.Name?.GetHashCode(StringComparison.Ordinal) ?? 0;
    }
}
