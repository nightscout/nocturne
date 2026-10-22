using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// An <see cref="INocturneDbContextFactory"/> for tests that supply contexts from one source: every
/// entry point takes a context from <see cref="CreateAsync"/> and pins it as that entry point does.
/// No test context has a scope, so the system pin differs from the tenant pin only in name.
/// </summary>
public abstract class SingleReaderDbContextFactory : INocturneDbContextFactory
{
    /// <inheritdoc />
    public abstract ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default);

    /// <inheritdoc />
    public async ValueTask<NocturneDbContext> CreateForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        var context = await CreateAsync(ct);
        context.TenantId = tenantId;
        return context;
    }

    /// <inheritdoc />
    public async ValueTask<NocturneDbContext> CreateForSubjectAsync(Guid subjectId, CancellationToken ct = default)
    {
        var context = await CreateAsync(ct);
        context.TenantId = Guid.Empty;
        context.SubjectId = subjectId;
        return context;
    }

    /// <inheritdoc />
    public async ValueTask<NocturneDbContext> CreatePlatformAsync(CancellationToken ct = default)
    {
        var context = await CreateAsync(ct);
        context.TenantId = Guid.Empty;
        return context;
    }

    /// <inheritdoc />
    public ValueTask<NocturneDbContext> CreateSystemPinnedAsync(Guid tenantId, CancellationToken ct = default) =>
        CreateForTenantAsync(tenantId, ct);
}
