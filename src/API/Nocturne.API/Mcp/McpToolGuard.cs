using System.Text.Json;
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
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            var services = request.Services
                ?? throw new InvalidOperationException("An MCP tool call ran without request services.");
            var httpContext = services.GetRequiredService<IHttpContextAccessor>().HttpContext
                ?? throw new InvalidOperationException("An MCP tool call ran outside an HTTP request.");

            if (request.MatchedPrimitive is McpServerTool tool)
            {
                foreach (var required in tool.Metadata.OfType<McpRequireScopeAttribute>())
                {
                    if (!httpContext.HasScope(required.Scope))
                        throw new McpProtocolException(
                            $"The '{tool.ProtocolTool.Name}' tool needs the '{required.Scope}' scope, "
                            + "which this token does not hold.",
                            McpErrorCode.InvalidRequest);
                }
            }

            var result = await next(request, cancellationToken);

            await AuditAsync(services, httpContext, request.Params, result, cancellationToken);

            return result;
        };

    private static async Task AuditAsync(
        IServiceProvider services,
        HttpContext httpContext,
        CallToolRequestParams? call,
        CallToolResult result,
        CancellationToken cancellationToken)
    {
        if (httpContext.GetTenantContext() is not { } tenant)
            return;

        var config = await services.GetRequiredService<ITenantAuditConfigCache>()
            .GetConfigAsync(tenant.TenantId, cancellationToken);
        if (!config.ReadAuditEnabled)
            return;

        services.GetRequiredService<ReadAccessLogWriter>().Enqueue(BuildAuditEntry(
            tenant.TenantId,
            call,
            result,
            services.GetRequiredService<McpToolReadLog>(),
            services.GetRequiredService<IAuditContext>(),
            httpContext,
            DateTime.UtcNow));
    }

    internal static ReadAccessLogEntity BuildAuditEntry(
        Guid tenantId,
        CallToolRequestParams? call,
        CallToolResult result,
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
            QueryParametersJson = call?.Arguments is { Count: > 0 } arguments
                ? JsonSerializer.Serialize(arguments)
                : null,
            TraceId = audit.TraceId,
            StatusCode = result.IsError == true
                ? StatusCodes.Status500InternalServerError
                : StatusCodes.Status200OK,
            CreatedAt = now,
        };
    }
}
