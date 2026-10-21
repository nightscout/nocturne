using Microsoft.AspNetCore.SignalR;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Hubs;

/// <summary>
/// SignalR hub filter that carries the handshake request's reader into every hub method invocation
/// and lifetime event: the <see cref="TenantContext"/> that
/// <see cref="Multitenancy.TenantResolutionMiddleware"/> stored in <c>HttpContext.Items</c>, and the
/// share, categories, full history and history clamp on the handshake scope's
/// <see cref="ICategoryReadContext"/> (<see cref="TenantReader"/>).
/// </summary>
/// <remarks>
/// SignalR builds a fresh DI scope per invocation, whose <see cref="ICategoryReadContext"/> is a
/// non-share with no clamp until this runs. The scope set here is the invocation's own,
/// <see cref="HubInvocationContext.ServiceProvider"/>, which covers services resolved from it inside
/// a method body.
///
/// It covers the hub's constructor dependencies too, although <c>DefaultHubDispatcher</c> activates
/// the hub from the invocation scope before the filter pipeline runs: a context from
/// <c>INocturneDbContextFactory</c> reads its scope live rather than at construction, so one opened
/// from a factory injected into the hub reaches the reader set here. A service that copies the tenant
/// or the clamp out of its scope in its constructor would still see none.
///
/// The handshake scope's state is read at each invocation, so a clamp an in-band credential applies
/// there (<see cref="HubAuthorizationState.Grant"/>) reaches every later invocation.
/// <c>TenantHubFilterTests</c> pins what the invocation scope carries.
/// </remarks>
public class TenantHubFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        CarryReader(invocationContext.Context, invocationContext.ServiceProvider);

        return await next(invocationContext);
    }

    public Task OnConnectedAsync(
        HubLifetimeContext context,
        Func<HubLifetimeContext, Task> next)
    {
        // Tenant validation is handled by TenantAwareHub.OnConnectedAsync. The accessor is still set
        // here for any OnConnectedAsync logic in derived hubs that uses tenant-scoped services.
        CarryReader(context.Context, context.ServiceProvider);

        return next(context);
    }

    public Task OnDisconnectedAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        CarryReader(context.Context, context.ServiceProvider);

        return next(context, exception);
    }

    private static void CarryReader(HubCallerContext callerContext, IServiceProvider services)
    {
        if (callerContext.GetHttpContext() is not { } handshake
            || handshake.GetTenantContext() is not { } tenantContext)
        {
            return;
        }

        TenantReader.Of(handshake.RequestServices).ForTenant(tenantContext).CarryInto(services);
    }
}
