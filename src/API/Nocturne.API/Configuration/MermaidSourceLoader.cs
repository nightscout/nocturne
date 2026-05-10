namespace Nocturne.API.Configuration;

internal static class MermaidSourceLoader
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
}
