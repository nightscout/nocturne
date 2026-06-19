using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Nocturne.API.Configuration;

/// <summary>
/// Loads OpenAPI tag descriptions from folder-based markdown sources under
/// <c>docs/api-descriptions</c>. Each <c>.md</c> file is one tag: the file name (sans
/// extension) is the tag name, and the top-level folder is the OpenAPI document it
/// belongs to (<c>nocturne</c> or <c>nightscout</c>). Deeper subfolders are purely
/// organizational. Optional YAML frontmatter carries <c>displayName</c> (sidebar label
/// override) and <c>standalone</c> (a conceptual page with no operations of its own).
/// </summary>
internal static class TagDescriptionLoader
{
    public sealed record TagDescription(
        string TagName,
        string Document,
        string? DisplayName,
        bool Standalone,
        string Markdown);

    /// <summary>
    /// Resolves the descriptions directory. In local dev the source tree is available at
    /// <c>ContentRootPath/../../../docs/api-descriptions</c>; in Docker the .md files are
    /// published to <c>wwwroot/api-descriptions</c> via an MSBuild target.
    /// </summary>
    public static string ResolveDescriptionsDir(IWebHostEnvironment env)
    {
        var devPath = Path.GetFullPath(
            Path.Combine(env.ContentRootPath, "..", "..", "..", "docs", "api-descriptions"));
        if (Directory.Exists(devPath))
            return devPath;

        return Path.Combine(env.WebRootPath, "api-descriptions");
    }

    public static IReadOnlyList<TagDescription> Load(IWebHostEnvironment env)
    {
        var dir = ResolveDescriptionsDir(env);
        if (!Directory.Exists(dir))
            return [];

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var results = new List<TagDescription>();

        foreach (var path in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(dir, path).Replace('\\', '/');

            // The first path segment is the document folder. Files sitting directly in the
            // root (e.g. README.md) have no document and are skipped.
            var separator = relative.IndexOf('/');
            if (separator < 0)
                continue;

            var document = relative[..separator];
            var tagName = Path.GetFileNameWithoutExtension(path);

            var (frontmatter, body) = SplitFrontmatter(File.ReadAllText(path));

            var meta = frontmatter is null
                ? new Frontmatter()
                : deserializer.Deserialize<Frontmatter>(frontmatter) ?? new Frontmatter();

            results.Add(new TagDescription(
                tagName,
                document,
                string.IsNullOrWhiteSpace(meta.DisplayName) ? null : meta.DisplayName,
                meta.Standalone,
                body.Trim()));
        }

        return results;
    }

    /// <summary>
    /// Splits an optional leading <c>---</c>-fenced YAML frontmatter block from the
    /// markdown body. Returns <c>(null, original)</c> when there is no frontmatter.
    /// </summary>
    private static (string? Frontmatter, string Body) SplitFrontmatter(string content)
    {
        var text = content.Replace("\r\n", "\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            return (null, text);

        var fenceEnd = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (fenceEnd < 0)
            return (null, text);

        var frontmatter = text[4..fenceEnd];

        // Body begins on the line after the closing fence.
        var bodyStart = text.IndexOf('\n', fenceEnd + 1);
        var body = bodyStart < 0 ? string.Empty : text[(bodyStart + 1)..];

        return (frontmatter, body);
    }

    private sealed class Frontmatter
    {
        public string? DisplayName { get; set; }
        public bool Standalone { get; set; }
    }
}
