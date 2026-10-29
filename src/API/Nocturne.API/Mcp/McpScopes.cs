using Nocturne.API.Authorization;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Mcp;

/// <summary>
/// The scopes an MCP client may hold. MCP is read-only, so these are read scopes, and they are the
/// categories the tools read: the dashboard chart's.
/// </summary>
internal static class McpScopes
{
    public static IReadOnlyList<string> Offered => ChartDataReadScopeGuard.AdmissionScopes;

    /// <summary>
    /// The scopes a grant or token for <paramref name="resource"/> may hold: all of them for the
    /// API (null), read scopes only for an MCP endpoint, whose tools read and whose tokens are held
    /// by the assistant's provider.
    /// </summary>
    public static IEnumerable<string> ForResource(IEnumerable<string> scopes, string? resource) =>
        resource is null ? scopes : scopes.Where(Scope.IsReadScope);
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

/// <summary>
/// A tool call whose arguments are wrong. The SDK returns its message as an <c>isError</c> result,
/// which the model reads and can act on; <see cref="McpToolGuard"/> audits it as 400, as MVC
/// audits a request that fails validation.
/// </summary>
internal sealed class McpInvalidArgumentsException(string message) : ModelContextProtocol.McpException(message);
