using Microsoft.AspNetCore.SignalR;
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
/// It does not reach the hub's constructor dependencies. <c>DefaultHubDispatcher</c> activates the hub
/// from the invocation scope before the filter pipeline runs (the hub instance is an argument of
/// <see cref="HubInvocationContext"/>), so a service that latches the tenant at construction — the
/// scoped <c>NocturneDbContext</c>, which reads the accessor in its factory — has already latched
/// <see cref="Guid.Empty"/> by the time this runs. Hub method bodies are unaffected because they
/// resolve tenant-scoped services from the handshake request's scope
/// (<c>Context.GetHttpContext().RequestServices</c>), which
/// <see cref="Multitenancy.TenantResolutionMiddleware"/> pinned.
///
/// No hub method resolves a service from the invocation scope today, so nothing currently depends on
/// this. It is here so that one which does gets the connection's tenant rather than
/// <see cref="Guid.Empty"/>, and <c>TenantHubFilterTests</c> pins that behaviour.
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
        if (callerContext.GetHttpContext()?.Items[TenantAwareHub.TenantContextKey]
            is not TenantContext tenantContext)
        {
            return;
        }

        services.GetRequiredService<ITenantAccessor>().SetTenant(tenantContext);
    }
}
