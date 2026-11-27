using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Nocturne.API.Extensions;
using Nocturne.API.Filters;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Reports;

/// <summary>
/// The read-audit row of a custom report run. A run is a POST, which <c>ReadAccessAuditFilter</c>
/// does not see, and its categories are the ones the datasets read (<see cref="ReportReadLog"/>),
/// not the route's.
/// </summary>
internal static class ReportReadAudit
{
    /// <summary>
    /// The most a row stores of the spec: what <c>ReadAccessAuditFilter</c> can be handed, since an
    /// MVC read's parameters arrive in a request line Kestrel caps at this length.
    /// </summary>
    internal static readonly int MaxArgumentsLength = new KestrelServerLimits().MaxRequestLineSize;

    internal const string Filtered = "[filtered]";

    /// <summary>Enqueues the row <paramref name="build"/> makes when the request's tenant audits reads.</summary>
    internal static Task WriteAsync(
        IServiceProvider services,
        HttpContext httpContext,
        string what,
        Func<Guid, ReadAccessLogEntity> build,
        CancellationToken cancellationToken) =>
        httpContext.GetTenantContext() is { } tenant
            ? WriteAsync(services, tenant.TenantId, what, build, cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// Enqueues the row <paramref name="build"/> makes when <paramref name="tenantId"/> audits reads, and says whether it
    /// did: not when the tenant does not audit reads or its setting could not be read.
    /// </summary>
    internal static async Task<bool> WriteAsync(
        IServiceProvider services,
        Guid tenantId,
        string what,
        Func<Guid, ReadAccessLogEntity> build,
        CancellationToken cancellationToken)
    {
        try
        {
            var config = await services.GetRequiredService<ITenantAuditConfigCache>()
                .GetConfigAsync(tenantId, cancellationToken);
            if (!config.ReadAuditEnabled)
                return false;
        }
        catch (Exception ex) when (StoreFailure.Is(ex))
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ReportReadAudit))
                .LogWarning(ex, "Could not read the read-audit setting; the report read {What} is not audited", what);
            return false;
        }

        services.GetRequiredService<ReadAccessLogWriter>().Enqueue(build(tenantId));
        return true;
    }

    /// <summary>A read-audit row for a report read: the categories and count <paramref name="readLog"/> holds.</summary>
    internal static ReadAccessLogEntity BuildEntry(
        Guid tenantId,
        string endpoint,
        string? queryParametersJson,
        int statusCode,
        ReportReadLog readLog,
        IAuditContext audit,
        HttpContext? httpContext,
        DateTime now)
    {
        var userAgent = httpContext?.Request.Headers.UserAgent.ToString();

        return new ReadAccessLogEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ActorSubjectId = audit.SubjectId,
            ActorName = audit.SubjectName,
            AuthType = audit.AuthType,
            TokenId = audit.TokenId,
            CredentialFingerprint = httpContext?.GetAuthContext()?.CredentialFingerprint,
            IpAddress = audit.IpAddress,
            UserAgent = string.IsNullOrEmpty(userAgent) ? null : userAgent,
            Endpoint = endpoint,
            EntityType = readLog.Datasets.Count == 0 ? null : string.Join(',', readLog.Datasets),
            RecordCount = readLog.RecordCount,
            QueryParametersJson = queryParametersJson,
            TraceId = audit.TraceId,
            StatusCode = statusCode,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// <c>{"spec": ...}</c> as JSON no longer than <see cref="MaxArgumentsLength"/>; a longer spec is
    /// stored as <see cref="Filtered"/>, so the row always holds valid JSON.
    /// </summary>
    internal static string SpecArgument(JsonElement spec)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object> { ["spec"] = spec });
        return json.Length <= MaxArgumentsLength
            ? json
            : JsonSerializer.Serialize(new Dictionary<string, object> { ["spec"] = Filtered });
    }
}
