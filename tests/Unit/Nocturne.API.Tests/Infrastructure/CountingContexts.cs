using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>Counts every context a decomposer opens on its own, each for one query.</summary>
internal sealed class CountingContexts(INocturneDbContextFactory inner, Action opened) : INocturneDbContextFactory
{
    public ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default)
    {
        opened();
        return inner.CreateAsync(ct);
    }

    public ValueTask<NocturneDbContext> CreateForTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        inner.CreateForTenantAsync(tenantId, ct);

    public ValueTask<NocturneDbContext> CreateForSubjectAsync(Guid subjectId, CancellationToken ct = default) =>
        inner.CreateForSubjectAsync(subjectId, ct);

    public ValueTask<NocturneDbContext> CreatePlatformAsync(CancellationToken ct = default) =>
        inner.CreatePlatformAsync(ct);

    public ValueTask<NocturneDbContext> CreateSystemPinnedAsync(Guid tenantId, CancellationToken ct = default) =>
        inner.CreateSystemPinnedAsync(tenantId, ct);
}
