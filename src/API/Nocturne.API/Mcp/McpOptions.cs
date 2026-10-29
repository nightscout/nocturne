namespace Nocturne.API.Mcp;

/// <summary>
/// Instance switch for the MCP endpoint. When off, neither <c>/mcp</c> nor
/// <c>/.well-known/oauth-protected-resource</c> is served.
/// </summary>
public class McpOptions
{
    public const string SectionName = "Mcp";

    public bool Enabled { get; set; } = true;
}
