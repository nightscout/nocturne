using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Nocturne.API.OpenApi;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Nocturne.API.Configuration;

/// <summary>
/// Builds the Scalar sidebar. Each controller is one entry, described by its class summary and
/// grouped by its <c>[Tags]</c> area (<see cref="ControllerDocs"/>). The markdown pages under
/// <c>docs/api-descriptions</c> (<see cref="TagDescriptionLoader"/>) add what a controller cannot
/// say: a page named after an area is the overview of that group, and a <c>standalone</c> page is
/// an entry of one of the <see cref="Sidebar"/> page groups. Diagrams mapped to a page in the
/// diagram manifest are appended to it.
/// </summary>
public sealed class TagDescriptionDocumentTransformer : IOpenApiDocumentTransformer
{
    /// <summary>
    /// The order of the sidebar groups of a document. An entry with pages is a group of standalone
    /// pages; an entry without is an area. An area that is not listed is shown last.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Group, string[]? Pages)[]> Sidebar =
        new Dictionary<string, (string, string[]?)[]>
        {
            ["nocturne"] =
            [
                ("Guides",
                [
                    "guide-authentication", "guide-data-model", "Syncing", "guide-realtime",
                    "guide-sharing", "guide-nightscout",
                ]),
                ("Authentication", null),
                ("Identity", null),
                ("Glucose", null),
                ("Treatments", null),
                ("Devices", null),
                ("Health", null),
                ("Analytics", null),
                ("Monitoring", null),
                ("Profiles", null),
                ("Metadata", null),
                ("Connectors", null),
                ("Platform", null),
                ("TenantAdmin", null),
                ("PlatformAdmin", null),
                ("Architecture",
                    ["arch-codebase", "arch-request-flow", "arch-deployment", "arch-type-safety"]),
            ],
            ["nightscout"] =
            [
                ("V1", null),
                ("V2", null),
                ("V3", null),
                ("Data Model",
                    ["ns-model-entries", "ns-model-treatments", "ns-model-devicestatus", "ns-model-profile"]),
            ],
        };

    private const string OverviewLabel = "Overview";
    private const string NoArea = "Other";

    private readonly Dictionary<string, List<DiagramRef>> _pageDiagrams;
    private readonly IReadOnlyList<TagDescriptionLoader.TagDescription> _pages;

    public TagDescriptionDocumentTransformer(IWebHostEnvironment env)
    {
        _pageDiagrams = BuildTagDiagramMap(env);
        _pages = TagDescriptionLoader.Load(env);
    }

    /// <summary>The tag of the overview page of an area.</summary>
    public static string OverviewTag(string area) => "overview-" + ControllerDocs.Slug(area);

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        var used = UsedTags(document);
        var controllers = context.DescriptionGroups
            .SelectMany(group => group.Items)
            .Select(ControllerDocs.For)
            .OfType<ControllerDocs.Entry>()
            .Where(entry => used.Contains(entry.Tag))
            .DistinctBy(entry => entry.Tag)
            .OrderBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pages = _pages
            .Where(page => page.Document == context.DocumentName)
            .ToDictionary(page => page.TagName, StringComparer.Ordinal);

        var tags = new List<OpenApiTag>();
        var groups = new JsonArray();
        var areas = controllers.Select(entry => entry.Area ?? NoArea).Distinct().ToList();

        foreach (var (group, standalone) in Sidebar.GetValueOrDefault(context.DocumentName, []))
        {
            if (standalone is not null)
            {
                var present = standalone.Where(pages.ContainsKey).ToList();
                foreach (var name in present)
                    tags.Add(Tag(name, PageBody(pages[name]), pages[name].DisplayName));
                AddGroup(groups, group, present);
            }
            else if (areas.Remove(group))
            {
                AddArea(group);
            }
        }

        foreach (var area in areas.OrderBy(area => area, StringComparer.OrdinalIgnoreCase).ToList())
            AddArea(area);

        document.Tags = new HashSet<OpenApiTag>(tags);
        document.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        document.Extensions["x-tagGroups"] = new JsonNodeExtension(groups);

        return Task.CompletedTask;

        void AddArea(string area)
        {
            var members = new List<string>();
            var label = area;

            if (pages.TryGetValue(area, out var overview) && !overview.Standalone)
            {
                label = overview.DisplayName ?? area;
                tags.Add(Tag(OverviewTag(area), PageBody(overview), OverviewLabel));
                members.Add(OverviewTag(area));
            }

            foreach (var entry in controllers.Where(entry => (entry.Area ?? NoArea) == area))
            {
                tags.Add(Tag(entry.Tag, entry.Summary, entry.Label == entry.Tag ? null : entry.Label));
                members.Add(entry.Tag);
            }

            AddGroup(groups, label, members);
        }
    }

    private string? PageBody(TagDescriptionLoader.TagDescription page)
    {
        if (!_pageDiagrams.TryGetValue(page.TagName, out var diagrams))
            return page.Markdown;

        var body = new System.Text.StringBuilder(page.Markdown).AppendLine().AppendLine();
        body.AppendLine("## Data Model").AppendLine();

        foreach (var diagram in diagrams)
        {
            body.AppendLine($"**{diagram.Title}**");
            if (!string.IsNullOrWhiteSpace(diagram.Description))
                body.AppendLine($"_{diagram.Description}_");
            body.AppendLine();
            body.AppendLine("```mermaid");
            body.AppendLine(diagram.MermaidSource);
            body.AppendLine("```");
            body.AppendLine();
        }

        return body.ToString().TrimEnd();
    }

    private static OpenApiTag Tag(string name, string? description, string? displayName)
    {
        var tag = new OpenApiTag { Name = name, Description = description };
        if (displayName is not null)
        {
            tag.Extensions = new Dictionary<string, IOpenApiExtension>
            {
                ["x-displayName"] = new JsonNodeExtension(JsonValue.Create(displayName)),
            };
        }

        return tag;
    }

    private static void AddGroup(JsonArray groups, string name, IReadOnlyCollection<string> members)
    {
        if (members.Count == 0)
            return;

        var tags = new JsonArray();
        foreach (var member in members)
            tags.Add(JsonValue.Create(member));

        groups.Add(new JsonObject { ["name"] = name, ["tags"] = tags });
    }

    private static HashSet<string> UsedTags(OpenApiDocument document)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in document.Paths.Values)
        {
            if (path.Operations is null) continue;
            foreach (var operation in path.Operations.Values)
            {
                if (operation.Tags is null) continue;
                foreach (var tag in operation.Tags)
                    if (tag.Name is not null)
                        used.Add(tag.Name);
            }
        }

        return used;
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
}
