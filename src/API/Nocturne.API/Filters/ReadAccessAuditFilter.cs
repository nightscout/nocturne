using System.Collections;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Filters;

/// <summary>
/// Result filter that logs V4 PHI read access to the audit log.
/// Runs after the action produces a result; failures are swallowed to never block requests.
/// </summary>
public class ReadAccessAuditFilter : IAsyncResultFilter
{
    private static readonly HashSet<string> WhitelistedParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "from", "to", "limit", "offset", "sort", "device", "source", "profileName", "timestamp"
    };

    private readonly ITenantAuditConfigCache _configCache;
    private readonly IAuditContext _auditContext;
    private readonly IDbContextFactory<NocturneDbContext> _contextFactory;
    private readonly ILogger<ReadAccessAuditFilter> _logger;

    public ReadAccessAuditFilter(
        ITenantAuditConfigCache configCache,
        IAuditContext auditContext,
        IDbContextFactory<NocturneDbContext> contextFactory,
        ILogger<ReadAccessAuditFilter> logger)
    {
        _configCache = configCache;
        _auditContext = auditContext;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        await next();

        try
        {
            var httpContext = context.HttpContext;
            var path = httpContext.Request.Path.Value;
            var method = httpContext.Request.Method;

            // Only audit V4 endpoints
            if (path is null || !path.StartsWith("/api/v4/", StringComparison.OrdinalIgnoreCase))
                return;

            // Only audit read operations
            if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
                return;

            // Skip auth failures — no meaningful read occurred
            var statusCode = httpContext.Response.StatusCode;
            if (statusCode is 401 or 403)
                return;

            // Resolve tenant
            if (httpContext.Items["TenantContext"] is not TenantContext tenantContext)
                return;

            // Check if read audit is enabled for this tenant
            var config = await _configCache.GetConfigAsync(tenantContext.TenantId);
            if (!config.ReadAuditEnabled)
                return;

            // Extract result metadata (best-effort)
            var (recordCount, entityType) = ExtractResultMetadata(context.Result);

            // Sanitize query parameters
            var queryParams = SanitizeQueryParameters(httpContext.Request.Query);

            // Get API secret hash prefix if auth type is ApiSecret
            string? apiSecretHashPrefix = null;
            if (string.Equals(_auditContext.AuthType, "ApiSecret", StringComparison.OrdinalIgnoreCase))
                apiSecretHashPrefix = httpContext.Items["ApiSecretHashPrefix"] as string;

            var userAgent = httpContext.Request.Headers.UserAgent.ToString();

            var entry = new ReadAccessLogEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantContext.TenantId,
                SubjectId = _auditContext.SubjectId,
                SubjectName = _auditContext.SubjectName,
                AuthType = _auditContext.AuthType,
                TokenId = _auditContext.TokenId,
                ApiSecretHashPrefix = apiSecretHashPrefix,
                IpAddress = _auditContext.IpAddress,
                UserAgent = string.IsNullOrEmpty(userAgent) ? null : userAgent,
                Endpoint = $"{method} {path}",
                EntityType = entityType,
                RecordCount = recordCount,
                QueryParametersJson = queryParams,
                CorrelationId = _auditContext.CorrelationId,
                StatusCode = statusCode,
                CreatedAt = DateTime.UtcNow,
            };

            // Fire-and-forget — non-blocking DB write
            _ = WriteAuditLogAsync(entry, tenantContext.TenantId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prepare read access audit log entry");
        }
    }

    /// <summary>
    /// Extracts record count and entity type from the action result (best-effort).
    /// </summary>
    internal static (int? RecordCount, string? EntityType) ExtractResultMetadata(IActionResult result)
    {
        if (result is not ObjectResult { Value: not null } objectResult)
            return (null, null);

        var value = objectResult.Value;
        var valueType = value.GetType();

        // Check for PaginatedResponse<T>
        if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(PaginatedResponse<>))
        {
            var dataProperty = valueType.GetProperty("Data");
            if (dataProperty?.GetValue(value) is IEnumerable data)
            {
                var count = data is ICollection c ? c.Count : data.Cast<object>().Count();
                var elementType = valueType.GetGenericArguments()[0];
                return (count, elementType.Name);
            }
        }

        // Check for ICollection
        if (value is ICollection collection)
        {
            var elementType = valueType.IsGenericType
                ? valueType.GetGenericArguments()[0]
                : valueType.IsArray
                    ? valueType.GetElementType()
                    : null;
            return (collection.Count, elementType?.Name);
        }

        // Single object
        return (1, valueType.Name);
    }

    /// <summary>
    /// Sanitizes query parameters, preserving whitelisted values and filtering unknown ones.
    /// </summary>
    internal static string? SanitizeQueryParameters(IQueryCollection query)
    {
        if (!query.Any())
            return null;

        var sanitized = new Dictionary<string, string>();
        foreach (var param in query)
        {
            sanitized[param.Key] = WhitelistedParams.Contains(param.Key)
                ? param.Value.ToString()
                : "[filtered]";
        }

        return JsonSerializer.Serialize(sanitized);
    }

    private async Task WriteAuditLogAsync(ReadAccessLogEntity entry, Guid tenantId)
    {
        try
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            ctx.TenantId = tenantId;
            ctx.ReadAccessLog.Add(entry);
            await ctx.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write read access audit log entry for {Endpoint}", entry.Endpoint);
        }
    }
}
