namespace Nocturne.Aspire.Scalar;

/// <summary>
/// References the mermaid renderer static assets spliced into the Scalar docs page head.
/// The JS and CSS live in each project's wwwroot — <c>ScalarBootstrap/wwwroot/mermaid-loader.*</c>
/// for the Aspire sidecar path and <c>Nocturne.API/wwwroot/scalar/mermaid-loader.*</c> for
/// direct API access. Both resolve to the browser URL <c>/scalar/mermaid-loader.*</c>.
/// </summary>
public static class MermaidLazyLoader
{
    public const string HeadContent = """
        <link rel="stylesheet" href="/scalar/mermaid-loader.css">
        <script type="module" src="/scalar/mermaid-loader.js"></script>
        """;
}
