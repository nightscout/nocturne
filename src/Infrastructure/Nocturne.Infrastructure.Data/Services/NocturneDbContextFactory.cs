using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// The one way outside Infrastructure to get a <see cref="NocturneDbContext"/>. Every context it
/// creates reads the share, history-clamp and audit state of the DI scope the factory was resolved
/// from (<see cref="TenantReadContext"/>), live; the entry points differ only in which tenant or
/// subject the context reaches, so a pin can narrow what a reader sees but never widen it. The one
/// exception is <see cref="CreateSystemPinnedAsync"/>.
/// Dispose every context it returns (use <c>await using</c>).
/// </summary>
/// <remarks>
/// Resolved from the root provider (a singleton or a hosted service) the factory has no scope, and
/// its contexts read <see cref="TenantReadContext.FailClosed"/>: no tenant, not a share, no clamp, no
/// audit actor. A root-resolved factory never reads any request's scope. Background work that needs
/// a tenant's scope state opens a scope with the tenant set and resolves the factory there.
/// <para>
/// Each open context holds its own pooled connection while it runs a query, so a caller that runs
/// several reads in one request awaits them in turn: concurrent awaits would take a connection per
/// read for that one request.
/// </para>
/// </remarks>
public interface INocturneDbContextFactory
{
    /// <summary>Creates a context that reads as the scope's reader, including its resolved tenant.</summary>
    ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default);

    /// <summary>Creates a context pinned to <paramref name="tenantId"/> in place of the scope's tenant.</summary>
    ValueTask<NocturneDbContext> CreateForTenantAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Creates a context with no tenant, pinned to <paramref name="subjectId"/>, for a read of one
    /// subject's own rows across tenants. It sets <c>app.current_subject_id</c>, so the
    /// <c>subject_pin</c> policy narrows <c>subjects</c>, <c>tenant_members</c> and
    /// <c>refresh_tokens</c> to that subject's rows. Every other entry point leaves the GUC unset,
    /// and the policy then admits every row.
    /// </summary>
    ValueTask<NocturneDbContext> CreateForSubjectAsync(Guid subjectId, CancellationToken ct = default);

    /// <summary>
    /// Creates a context with no tenant and no subject, for platform tables. Tenant-scoped tables
    /// read empty through it.
    /// </summary>
    ValueTask<NocturneDbContext> CreatePlatformAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates a context pinned to <paramref name="tenantId"/> that reads as no reader: not a share,
    /// no categories, no history clamp. The scope's audit actor is still carried.
    /// </summary>
    /// <remarks>
    /// For an allowlist of instance-level reads that a public-share request needs answered but may
    /// not see as a reader: whether the tenant is past setup or has an orphaned member, and its
    /// owners. A singleton has no use for it, since its root-resolved factory already reads as no
    /// reader. The identity tables these read (tenant_members, member_roles, tenant_roles,
    /// subjects, passkey credentials, OIDC identities) are not tenant-scoped and carry no tenant
    /// policy, so a tenant pin returns the same rows; this entry point keeps those reads working
    /// if they come under one, since a share is hidden every table no category governs.
    /// <c>SystemPinnedAllowlistTests</c> pins the calling members.
    /// </remarks>
    ValueTask<NocturneDbContext> CreateSystemPinnedAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>
/// The root <see cref="IServiceProvider"/>, captured by a singleton so a transient
/// <see cref="NocturneDbContextFactory"/> can tell whether it was resolved from the root.
/// </summary>
internal sealed class RootServiceProvider(IServiceProvider root)
{
    public IServiceProvider Root { get; } = root;
}

/// <summary>
/// Transient so that each resolution captures the provider it was resolved from: a scope's
/// <see cref="TenantReadContext"/>, or none at the root.
/// </summary>
internal sealed class NocturneDbContextFactory(
    IDbContextFactory<NocturneDbContext> contexts,
    IServiceProvider services,
    RootServiceProvider root) : INocturneDbContextFactory
{
    private readonly TenantReadContext _readContext = ReferenceEquals(services, root.Root)
        ? TenantReadContext.FailClosed
        : services.GetRequiredService<TenantReadContext>();

    public ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(Create());

    public ValueTask<NocturneDbContext> CreateForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        var context = Create();
        context.TenantId = tenantId;
        return ValueTask.FromResult(context);
    }

    public ValueTask<NocturneDbContext> CreateForSubjectAsync(Guid subjectId, CancellationToken ct = default)
    {
        var context = Create();
        context.TenantId = Guid.Empty;
        context.SubjectId = subjectId;
        return ValueTask.FromResult(context);
    }

    public ValueTask<NocturneDbContext> CreatePlatformAsync(CancellationToken ct = default)
    {
        var context = Create();
        context.TenantId = Guid.Empty;
        return ValueTask.FromResult(context);
    }

    public ValueTask<NocturneDbContext> CreateSystemPinnedAsync(Guid tenantId, CancellationToken ct = default)
    {
        var context = contexts.CreateDbContext();
        context.ReadContext = new TenantReadContext(null, null, _readContext.AuditContext);
        context.TenantId = tenantId;
        return ValueTask.FromResult(context);
    }

    internal NocturneDbContext Create()
    {
        var context = contexts.CreateDbContext();
        context.ReadContext = _readContext;
        return context;
    }
}
