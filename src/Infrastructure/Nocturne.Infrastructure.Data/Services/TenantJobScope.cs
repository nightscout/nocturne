using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Opens the DI scope a job runs in: its <see cref="TenantReader"/> set on the scope's
/// <see cref="ITenantAccessor"/> and <see cref="ICategoryReadContext"/>, and its audit actor
/// system-attributed (<see cref="ISystemAttributable"/>). Every context the scope resolves or creates
/// reads both live (<see cref="INocturneDbContextFactory"/>), so nothing else needs pinning.
/// </summary>
/// <remarks>
/// <see cref="Open(IServiceProvider, TenantContext?, string)"/> reads as no one: full history, no
/// share. It is for work no caller is waiting on (a hosted loop, startup) or work that returns nothing
/// it read; <c>ChildScopeGuardTests</c> pins its callers. Work done for a caller uses
/// <see cref="OpenFor(IServiceProvider, TenantReader, string)"/>, so it cannot read more there than in
/// the caller's own scope.
/// <para>
/// System attribution matters beyond the audit log: the mutation interceptor derives
/// <c>DeletedByUser</c> from it, and a row soft-deleted by a user-attributed sync blocks a later
/// resync from re-creating it.
/// </para>
/// </remarks>
public static class TenantJobScope
{
    /// <param name="provider">The provider to open the scope from (usually the root).</param>
    /// <param name="tenant">The tenant the job works for, or null for a job with no tenant.</param>
    /// <param name="actor">Audit endpoint naming the job, e.g. <c>"connector:dexcom"</c>.</param>
    public static AsyncServiceScope Open(IServiceProvider provider, TenantContext? tenant, string actor) =>
        Prepare(provider.CreateAsyncScope(), TenantReader.NoOne(tenant), actor);

    /// <inheritdoc cref="Open(IServiceProvider, TenantContext?, string)"/>
    public static AsyncServiceScope Open(IServiceScopeFactory scopes, TenantContext? tenant, string actor) =>
        Prepare(scopes.CreateAsyncScope(), TenantReader.NoOne(tenant), actor);

    /// <param name="provider">The provider to open the scope from.</param>
    /// <param name="reader">The caller the work is done for, from <see cref="TenantReader.Of(IServiceProvider)"/>.</param>
    /// <param name="actor">Audit endpoint naming the job.</param>
    public static AsyncServiceScope OpenFor(IServiceProvider provider, TenantReader reader, string actor) =>
        Prepare(provider.CreateAsyncScope(), reader, actor);

    /// <inheritdoc cref="OpenFor(IServiceProvider, TenantReader, string)"/>
    public static AsyncServiceScope OpenFor(IServiceScopeFactory scopes, TenantReader reader, string actor) =>
        Prepare(scopes.CreateAsyncScope(), reader, actor);

    private static AsyncServiceScope Prepare(AsyncServiceScope scope, TenantReader reader, string actor)
    {
        try
        {
            reader.CarryInto(scope.ServiceProvider);

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
