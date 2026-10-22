using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// Gives a context a test built by hand the scope state an <see cref="INocturneDbContextFactory"/>
/// context reads (<see cref="TenantReadContext"/>), for a test that has no DI scope to resolve one from.
/// </summary>
public static class TestReadContext
{
    /// <summary>Reads <paramref name="audit"/> as its audit actor, and no tenant, share or clamp.</summary>
    public static NocturneDbContext AttributedTo(this NocturneDbContext context, IAuditContext? audit) =>
        context.ReadingFrom(audit: audit);

    /// <summary>Reads the given scope state live, as a context the factory creates in that scope does.</summary>
    public static NocturneDbContext ReadingFrom(
        this NocturneDbContext context,
        ITenantAccessor? tenant = null,
        ICategoryReadContext? reader = null,
        IAuditContext? audit = null)
    {
        context.ReadContext = new TenantReadContext(tenant, reader, audit);
        return context;
    }
}
