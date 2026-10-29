using Nocturne.API.Authorization;

namespace Nocturne.API.Mcp;

/// <summary>
/// The scopes an MCP client may hold. MCP is read-only, so these are read scopes, and they are the
/// categories the tools read: the dashboard chart's.
/// </summary>
internal static class McpScopes
{
    public static IReadOnlyList<string> Offered => ChartDataReadScopeGuard.AdmissionScopes;
}

/// <summary>
/// A scope a tool cannot run without. Checked by <see cref="McpToolGuard"/> before the tool runs;
/// a tool that reads further categories checks those itself and leaves them out when missing.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class McpRequireScopeAttribute(string scope) : Attribute
{
    public string Scope { get; } = scope;
}
