using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Nocturne.API.Authorization;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.RateLimiting;

/// <summary>
/// Bounds how often a delegated credential may start a connector sync: <see cref="PermitLimit"/>
/// per <see cref="Window"/> for each tenant and connector. Each sync can re-pull a connector's whole
/// history and signs in to the vendor at full rate, so an app looping on it would load the database
/// and risk locking the tenant out of their own vendor account. The person's own sign-in is not
/// limited here.
/// </summary>
/// <remarks>
/// It sits after authentication rather than in the rate-limiting middleware because it must tell an
/// app from the person, and the middleware runs before the credential is known (see
/// <see cref="Extensions.ServiceRegistrationExtensions.RateLimitPartitionKeys"/>).
/// </remarks>
public sealed class DelegatedConnectorSyncLimiter : IDisposable
{
    public const int PermitLimit = 6;

    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(
        key => RateLimitPartition.GetFixedWindowLimiter(
            key,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = PermitLimit, Window = Window, QueueLimit = 0 }));

    /// <summary>
    /// Takes one sync from the tenant's allowance for the connector <paramref name="executor"/> runs.
    /// Keyed on the registered id rather than anything the caller spells, so the partitions are at
    /// most tenants times connectors.
    /// </summary>
    public RateLimitLease Acquire(Guid tenantId, IConnectorSyncExecutor executor) =>
        _limiter.AttemptAcquire($"{tenantId}:{executor.ConnectorId}");

    public void Dispose() => _limiter.Dispose();
}

/// <summary>
/// Applies <see cref="DelegatedConnectorSyncLimiter"/> to an action whose <c>id</c> route value is
/// the connector, for a caller <see cref="RequireInteractiveSessionAttribute.Refuses"/>. An id no
/// sync executor answers to is a 404 for every caller, before any lease is taken.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LimitDelegatedConnectorSyncAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var httpContext = context.HttpContext;
        var services = httpContext.RequestServices;
        var connectorId = context.RouteData.Values["id"] as string;
        var executor = services.GetServices<IConnectorSyncExecutor>()
            .FirstOrDefault(e => e.ConnectorId.Equals(connectorId, StringComparison.OrdinalIgnoreCase));

        if (executor is null)
        {
            context.Result = ((ControllerBase)context.Controller).Problem(
                detail: $"Connector not found: {connectorId}",
                statusCode: StatusCodes.Status404NotFound,
                title: "Not Found");
            return;
        }

        if (RequireInteractiveSessionAttribute.Refuses(httpContext))
        {
            using var lease = services.GetRequiredService<DelegatedConnectorSyncLimiter>()
                .Acquire(services.GetRequiredService<ITenantAccessor>().TenantId, executor);

            if (!lease.IsAcquired)
            {
                context.Result = new RejectedResult(
                    lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : null);
                return;
            }
        }

        await next();
    }

    private sealed class RejectedResult(TimeSpan? retryAfter) : IActionResult
    {
        public Task ExecuteResultAsync(ActionContext context) =>
            RateLimitRejection.WriteAsync(context.HttpContext, retryAfter, context.HttpContext.RequestAborted);
    }
}
