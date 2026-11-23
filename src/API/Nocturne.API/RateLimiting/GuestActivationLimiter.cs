using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc.Filters;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.RateLimiting;

/// <summary>
/// Bounds guest code activations at one tenant from every client address together:
/// <see cref="PermitLimit"/> per <see cref="Window"/>. The <c>guest-activate</c> policy bounds one
/// address, which says nothing about a caller holding many, and a guess only ever matches the
/// resolved tenant's codes, so the tenant is what the guessing rate has to be bounded on.
/// </summary>
/// <remarks>
/// <para>
/// A code is 7 characters from 31, about 2.75e10 values, and a tenant holds at most a handful of live
/// codes at once. At 30 attempts per 10 minutes, five live codes take about 3,500 years to hit on
/// average, and the chance within one code's 48-hour life is under 1 in 500,000.
/// </para>
/// <para>
/// Every attempt is charged, the right code included, before it is checked, so concurrent requests
/// cannot all pass before any is counted. The ceiling leaves room for six people each mistyping
/// every attempt their own address allows. The cost is that whoever spends the budget, from enough
/// addresses to pass the per-address limit, refuses that tenant's guests until the window ends, and
/// can do so again in each window. Nothing is denied to other tenants, and a guest who already holds
/// a session is unaffected.
/// </para>
/// <para>
/// Held in process, as <see cref="DelegatedConnectorSyncLimiter"/> is: the API runs as one process.
/// A request with no resolved tenant shares the bucket of <see cref="Guid.Empty"/>.
/// </para>
/// </remarks>
public sealed class GuestActivationLimiter : IDisposable
{
    public const int PermitLimit = 30;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly PartitionedRateLimiter<Guid> _limiter = PartitionedRateLimiter.Create<Guid, Guid>(
        tenantId => RateLimitPartition.GetFixedWindowLimiter(
            tenantId,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = PermitLimit, Window = Window, QueueLimit = 0 }));

    /// <summary>Takes one activation attempt from <paramref name="tenantId"/>'s allowance.</summary>
    public RateLimitLease Acquire(Guid tenantId) => _limiter.AttemptAcquire(tenantId);

    public void Dispose() => _limiter.Dispose();
}

/// <summary>
/// Applies <see cref="GuestActivationLimiter"/> to the resolved tenant, refusing as the rate-limiting
/// middleware does.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LimitGuestActivationsPerTenantAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        using var lease = services.GetRequiredService<GuestActivationLimiter>()
            .Acquire(services.GetRequiredService<ITenantAccessor>().TenantId);

        if (!lease.IsAcquired)
        {
            context.Result = new RateLimitRejectionResult(
                lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : null);
            return;
        }

        await next();
    }
}
