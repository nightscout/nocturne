using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nocturne.API.Extensions;
using Nocturne.API.Filters;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Mcp;

/// <summary>
/// The one wrapper every tool call goes through: refuses a call whose token lacks a scope the tool
/// requires (<see cref="McpRequireScopeAttribute"/>), and writes the call's read-audit row when the
/// tenant audits reads.
/// </summary>
/// <remarks>
/// Tools are not MVC actions, so neither <c>[RequireScope]</c> nor <c>ReadAccessAuditFilter</c>
/// reaches them. In stateless mode the SDK runs the call on the request's own services, so the
/// request's <see cref="HttpContext"/>, tenant and audit context are the ones read here.
/// </remarks>
internal static class McpToolGuard
{
    /// <summary>
    /// The most a row stores of a call's arguments: what <c>ReadAccessAuditFilter</c> can be
    /// handed, since an MVC read's parameters arrive in a request line Kestrel caps at this length.
    /// </summary>
    internal static readonly int MaxArgumentsLength = new KestrelServerLimits().MaxRequestLineSize;

    internal const string Filtered = "[filtered]";

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            var services = request.Services
                ?? throw new InvalidOperationException("An MCP tool call ran without request services.");
            var httpContext = services.GetRequiredService<IHttpContextAccessor>().HttpContext
                ?? throw new InvalidOperationException("An MCP tool call ran outside an HTTP request.");
            var tool = request.MatchedPrimitive as McpServerTool;

            // Refused before anything is read, so no row: the MVC filter skips a 403 as well.
            foreach (var required in tool?.Metadata.OfType<McpRequireScopeAttribute>() ?? [])
            {
                if (!httpContext.HasScope(required.Scope))
                    throw new McpProtocolException(
                        $"The '{tool!.ProtocolTool.Name}' tool needs the '{required.Scope}' scope, "
                        + "which this token does not hold.",
                        McpErrorCode.InvalidRequest);
            }

            CallToolResult? result = null;
            try
            {
                result = await next(request, cancellationToken);
                return result;
            }
            finally
            {
                // A tool that threw (an argument out of range) reads as failed, as the MVC filter
                // audits an exception. A call the client abandoned is not recorded.
                if (!cancellationToken.IsCancellationRequested)
                    await AuditAsync(services, httpContext, tool, request.Params, result, cancellationToken);
            }
        };

    private static async Task AuditAsync(
        IServiceProvider services,
        HttpContext httpContext,
        McpServerTool? tool,
        CallToolRequestParams? call,
        CallToolResult? result,
        CancellationToken cancellationToken)
    {
        if (httpContext.GetTenantContext() is not { } tenant)
            return;

        try
        {
            var config = await services.GetRequiredService<ITenantAuditConfigCache>()
                .GetConfigAsync(tenant.TenantId, cancellationToken);
            if (!config.ReadAuditEnabled)
                return;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(McpToolGuard))
                .LogWarning(ex, "Could not read the read-audit setting; the MCP call {Tool} is not audited", call?.Name);
            return;
        }

        services.GetRequiredService<ReadAccessLogWriter>().Enqueue(BuildAuditEntry(
            tenant.TenantId,
            call,
            DeclaredArguments(tool),
            result,
            services.GetRequiredService<McpToolReadLog>(),
            services.GetRequiredService<IAuditContext>(),
            httpContext,
            DateTime.UtcNow));
    }

    /// <summary>The argument names a tool's input schema declares.</summary>
    internal static IReadOnlySet<string> DeclaredArguments(McpServerTool? tool) =>
        tool?.ProtocolTool.InputSchema is { ValueKind: JsonValueKind.Object } schema
        && schema.TryGetProperty("properties", out var properties)
        && properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>();

    internal static ReadAccessLogEntity BuildAuditEntry(
        Guid tenantId,
        CallToolRequestParams? call,
        IReadOnlySet<string> declaredArguments,
        CallToolResult? result,
        McpToolReadLog readLog,
        IAuditContext audit,
        HttpContext httpContext,
        DateTime now)
    {
        var userAgent = httpContext.Request.Headers.UserAgent.ToString();

        return new ReadAccessLogEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SubjectId = audit.SubjectId,
            SubjectName = audit.SubjectName,
            AuthType = audit.AuthType,
            TokenId = audit.TokenId,
            CredentialFingerprint = httpContext.GetAuthContext()?.CredentialFingerprint,
            IpAddress = audit.IpAddress,
            UserAgent = string.IsNullOrEmpty(userAgent) ? null : userAgent,
            Endpoint = $"MCP {call?.Name}",
            EntityType = readLog.Datasets.Count == 0 ? null : string.Join(',', readLog.Datasets),
            RecordCount = readLog.RecordCount,
            QueryParametersJson = SanitizeArguments(call?.Arguments, declaredArguments),
            TraceId = audit.TraceId,
            StatusCode = result is null || result.IsError == true
                ? StatusCodes.Status500InternalServerError
                : StatusCodes.Status200OK,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// The arguments the tool declares, as JSON no longer than <see cref="MaxArgumentsLength"/>:
    /// the largest values give way to <see cref="Filtered"/>, so the row always holds valid JSON.
    /// </summary>
    internal static string? SanitizeArguments(
        IDictionary<string, JsonElement>? arguments, IReadOnlySet<string> declared)
    {
        if (arguments is null)
            return null;

        var kept = arguments
            .Where(a => declared.Contains(a.Key))
            .ToDictionary(a => a.Key, a => (object)a.Value);
        if (kept.Count == 0)
            return null;

        var json = JsonSerializer.Serialize(kept);
        foreach (var largest in kept.OrderByDescending(a => ((JsonElement)a.Value).GetRawText().Length).Select(a => a.Key).ToList())
        {
            if (json.Length <= MaxArgumentsLength)
                break;
            kept[largest] = Filtered;
            json = JsonSerializer.Serialize(kept);
        }

        return json;
    }
}
