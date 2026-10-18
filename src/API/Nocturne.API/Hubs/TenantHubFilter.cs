using Microsoft.AspNetCore.SignalR;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Hubs;

/// <summary>
/// SignalR hub filter that populates the <see cref="ITenantAccessor"/> for every hub method
/// invocation and lifetime event, from the <see cref="TenantContext"/> that
/// <see cref="Multitenancy.TenantResolutionMiddleware"/> stored in <c>HttpContext.Items</c> during the
/// upgrade handshake.
/// </summary>
/// <remarks>
/// The accessor is scoped and SignalR builds a fresh DI scope per invocation, so the scope set here is
/// the invocation's own — <see cref="HubInvocationContext.ServiceProvider"/> — which covers services
/// resolved from it inside a method body.
///
/// It covers the hub's constructor dependencies too, although <c>DefaultHubDispatcher</c> activates
/// the hub from the invocation scope before the filter pipeline runs: a context from
/// <c>INocturneDbContextFactory</c> reads its scope's accessor live rather than at construction, so
/// one opened from a factory injected into the hub reaches the tenant set here. A service that copies
/// the tenant out of the accessor in its constructor would still see none. Hub method bodies that
/// resolve services from the handshake request's scope (<c>Context.GetHttpContext().RequestServices</c>)
/// read the tenant <see cref="Multitenancy.TenantResolutionMiddleware"/> set on that scope's accessor.
///
/// <c>TenantHubFilterTests</c> pins that the invocation scope carries the connection's tenant.
/// </remarks>
public class TenantHubFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        SetTenant(invocationContext.Context, invocationContext.ServiceProvider);

        return await next(invocationContext);
    }

    public Task OnConnectedAsync(
        HubLifetimeContext context,
        Func<HubLifetimeContext, Task> next)
    {
        // Tenant validation is handled by TenantAwareHub.OnConnectedAsync. The accessor is still set
        // here for any OnConnectedAsync logic in derived hubs that uses tenant-scoped services.
        SetTenant(context.Context, context.ServiceProvider);

        return next(context);
    }

    public Task OnDisconnectedAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        SetTenant(context.Context, context.ServiceProvider);

        return next(context, exception);
    }

    private static void SetTenant(HubCallerContext callerContext, IServiceProvider services)
    {
        if (callerContext.GetHttpContext()?.GetTenantContext() is not { } tenantContext)
        {
            return;
        }

        services.GetRequiredService<ITenantAccessor>().SetTenant(tenantContext);
    }
}
