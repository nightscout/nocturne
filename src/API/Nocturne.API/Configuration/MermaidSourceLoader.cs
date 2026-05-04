namespace Nocturne.API.Configuration;

internal static class MermaidSourceLoader
{
    public static string? TryRead(string diagramsDir, string source)
    {
        var path = Path.Combine(diagramsDir, source);
        return File.Exists(path) ? File.ReadAllText(path).TrimEnd() : null;
    }
}
