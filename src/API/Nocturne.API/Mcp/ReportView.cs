namespace Nocturne.API.Mcp;

/// <summary>
/// The single-file HTML of <c>@nocturne/mcp-view</c>. The build embeds it; in Development the
/// copy under <c>src/Web/packages/mcp-view/dist</c> wins when present, so a rebuilt view is served
/// without restarting the API.
/// </summary>
internal sealed class ReportView(IWebHostEnvironment environment)
{
    internal const string ResourceName = "Nocturne.API.Mcp.report.html";

    private static readonly string[] DevRelativePath = ["src", "Web", "packages", "mcp-view", "dist", "report.html"];

    private readonly Lazy<string?> _embedded = new(ReadEmbedded);
    private readonly Lazy<string?> _devPath = new(FindDevPath);

    public string? GetHtml()
    {
        if (environment.IsDevelopment() && _devPath.Value is { } path && File.Exists(path))
            return File.ReadAllText(path);

        return _embedded.Value;
    }

    private static string? ReadEmbedded()
    {
        using var stream = typeof(ReportView).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? FindDevPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, .. DevRelativePath]);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
