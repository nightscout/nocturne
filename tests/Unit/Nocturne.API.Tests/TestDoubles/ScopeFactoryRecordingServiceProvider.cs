using Microsoft.Extensions.DependencyInjection;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>
/// Request services that record whether the <see cref="IServiceScopeFactory"/> was resolved.
/// </summary>
/// <remarks>
/// Code that hands work to a background task resolves the scope factory on the request thread and
/// lets the task open its own scope, so a request that never resolved it never dispatched. That turns
/// "nothing was dispatched" into a synchronous assertion instead of a fixed wait followed by a
/// negative one. A test that relies on it pairs it with a positive test asserting the resolution
/// does happen when work is dispatched, so the check cannot pass vacuously.
/// </remarks>
internal sealed class ScopeFactoryRecordingServiceProvider(IServiceProvider inner) : IServiceProvider
{
    private int _scopeFactoryRequests;

    public bool ScopeFactoryRequested => Volatile.Read(ref _scopeFactoryRequests) > 0;

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IServiceScopeFactory))
            Interlocked.Increment(ref _scopeFactoryRequests);
        return inner.GetService(serviceType);
    }
}
