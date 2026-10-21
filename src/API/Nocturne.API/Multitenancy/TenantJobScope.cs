using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Multitenancy;

/// <summary>
/// Opens the DI scope a background job runs in: the tenant set on its <see cref="ITenantAccessor"/>
/// and its audit actor system-attributed (<see cref="ISystemAttributable"/>). Every context the
/// scope resolves or creates reads both live
/// (<see cref="Nocturne.Infrastructure.Data.Services.INocturneDbContextFactory"/>), so nothing else
/// needs pinning.
/// </summary>
/// <remarks>
/// System attribution matters beyond the audit log: the mutation interceptor derives
/// <c>DeletedByUser</c> from it, and a row soft-deleted by a user-attributed sync blocks a later
/// resync from re-creating it.
/// </remarks>
public static class TenantJobScope
{
    /// <param name="provider">The provider to open the scope from (usually the root).</param>
    /// <param name="tenant">The tenant the job works for, or null for a job with no tenant.</param>
    /// <param name="actor">Audit endpoint naming the job, e.g. <c>"connector:dexcom"</c>.</param>
    /// <param name="historyClamped">
    /// Carries a history-clamped caller's clamp into a scope opened on its behalf, so it cannot read
    /// more history there than in its own request. Share state is not carried: the only
    /// request-triggered caller (compression-low detection, glucose.readwrite) is never a share.
    /// </param>
    public static AsyncServiceScope Open(
        IServiceProvider provider, TenantContext? tenant, string actor, bool historyClamped = false) =>
        Prepare(provider.CreateAsyncScope(), tenant, actor, historyClamped);

    /// <inheritdoc cref="Open(IServiceProvider, TenantContext?, string, bool)"/>
    public static AsyncServiceScope Open(
        IServiceScopeFactory scopes, TenantContext? tenant, string actor, bool historyClamped = false) =>
        Prepare(scopes.CreateAsyncScope(), tenant, actor, historyClamped);

    private static AsyncServiceScope Prepare(
        AsyncServiceScope scope, TenantContext? tenant, string actor, bool historyClamped)
    {
        try
        {
            if (tenant is not null)
                scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(tenant);

            if (historyClamped)
                scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();

            if (scope.ServiceProvider.GetService<IAuditContext>() is ISystemAttributable audit)
                audit.AttributeToSystem(actor);

            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }
}
