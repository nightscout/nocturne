using Microsoft.AspNetCore.SignalR;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Hubs;

/// <summary>
/// SignalR hub filter that sets the connection's reader (<see cref="HubConnectionReader"/>: tenant,
/// share, categories, full history and history clamp) and audit actor (<see cref="HubConnectionActor"/>)
/// on every hub method invocation and lifetime event scope.
/// </summary>
/// <remarks>
/// SignalR builds a fresh DI scope per invocation, which reads as no one until this runs. The scope
/// set here is the invocation's own, <see cref="HubInvocationContext.ServiceProvider"/>, which covers
/// services resolved from it inside a method body.
///
/// It covers the hub's constructor dependencies too, although <c>DefaultHubDispatcher</c> activates
/// the hub from the invocation scope before the filter pipeline runs: a context from
/// <c>INocturneDbContextFactory</c> reads its scope live rather than at construction, so one opened
/// from a factory injected into the hub reaches the reader set here. A service that copies the tenant
/// or the clamp out of its scope in its constructor would still see none.
///
/// Before each invocation it resolves the connection's credential
/// (<see cref="HubAuthorizationState.Resolve"/>), so the clamp of a credential presented on the
/// upgrade request applies from the first call, ahead of <see cref="HubAuthorizationFilter"/>. On
/// connect it also sets the reader and the actor on the upgrade request's scope when that scope still
/// reads as no one, which is the long-polling copy; the hub methods that read and write through that
/// scope then do so as the connection. SignalR runs one invocation per connection at a time
/// (<c>MaximumParallelInvocationsPerClient</c>, pinned at 1 in <c>AddSignalR</c>), so an in-band grant
/// cannot race another call's carry. <c>TenantHubFilterTests</c> pins what each scope carries.
/// </remarks>
public class TenantHubFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        HubAuthorizationState.Resolve(invocationContext.Context);
        CarryReader(invocationContext.Context, invocationContext.ServiceProvider);

        return await next(invocationContext);
    }

    public Task OnConnectedAsync(
        HubLifetimeContext context,
        Func<HubLifetimeContext, Task> next)
    {
        if (context.Context.GetHttpContext()?.RequestServices is { } upgradeScope
            && HubConnectionReader.Of(context.Context) is { } reader
            && TenantReader.ReadsAsNoOne(upgradeScope))
        {
            reader.CarryInto(upgradeScope);
            HubConnectionActor.CarryInto(context.Context, upgradeScope);
        }

        // Tenant validation is handled by TenantAwareHub.OnConnectedAsync. The reader is still set
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

    private static void CarryReader(HubCallerContext connection, IServiceProvider services)
    {
        HubConnectionReader.Of(connection)?.CarryInto(services);
        HubConnectionActor.CarryInto(connection, services);
    }
}
