using System.Text.RegularExpressions;

namespace Nocturne.API.Configuration;

internal static partial class MermaidSourceLoader
{
    /// <summary>
    /// Resolves the diagrams directory. In local dev the source tree is available
    /// at <c>ContentRootPath/../../../docs/diagrams</c>; in Docker the .mmd files
    /// are published to <c>wwwroot/diagrams</c> via a MSBuild target.
    /// </summary>
    public static string ResolveDiagramsDir(IWebHostEnvironment env)
    {
        var devPath = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "..", "..", "docs", "diagrams"));
        if (Directory.Exists(devPath))
            return devPath;

        return Path.Combine(env.WebRootPath, "diagrams");
    }

    public static string? TryRead(string diagramsDir, string source)
    {
        var path = Path.Combine(diagramsDir, source);
        return File.Exists(path) ? File.ReadAllText(path).TrimEnd() : null;
    }

    /// <summary>
    /// Replaces each <c>&lt;!-- diagram: name.mmd --&gt;</c> line of a page with that diagram as a
    /// fenced mermaid block. A diagram that is not on disk (the generated ones are absent from a
    /// plain checkout) leaves nothing behind.
    /// </summary>
    public static string EmbedDiagrams(string markdown, string diagramsDir) =>
        DiagramPlaceholder().Replace(markdown, match =>
        {
            var mermaid = TryRead(diagramsDir, Path.GetFileName(match.Groups["source"].Value));
            return mermaid is null ? string.Empty : $"```mermaid\n{mermaid}\n```";
        });

    [GeneratedRegex(@"^[ \t]*<!--\s*diagram:\s*(?<source>[\w.-]+\.mmd)\s*-->[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex DiagramPlaceholder();
}
