using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Services.Deduplication;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>The deduplication modules over one context factory, as the tests build them.</summary>
public static class DeduplicationModules
{
    public static DuplicateLinker Linker(INocturneDbContextFactory contexts) =>
        new(contexts, NullLogger<DuplicateLinker>.Instance);

    public static DuplicateReconciler Reconciler(
        INocturneDbContextFactory contexts, ILogger<DuplicateReconciler>? logger = null) =>
        new(contexts, logger ?? NullLogger<DuplicateReconciler>.Instance);

    /// <summary>The admin surface for <paramref name="tenantId"/>, as an unclamped caller.</summary>
    public static DeduplicationAdmin Admin(INocturneDbContextFactory contexts, Guid tenantId) =>
        new(contexts, Linker(contexts), Reconciler(contexts), NullLogger<DeduplicationAdmin>.Instance,
            new FixedTenantAccessor(new TenantContext(tenantId, "test", "Test", true, false)),
            new CategoryReadContext());

    /// <summary>
    /// Requests a full run and runs it to its end, slice after slice, as the job scheduler would.
    /// </summary>
    /// <returns>The run's final status.</returns>
    public static async Task<DeduplicationJobStatus> RunToEndAsync(
        this DeduplicationAdmin admin, CancellationToken ct = default)
    {
        var runId = await admin.StartRunAsync(ct);
        JobCursor? at = null;
        do
        {
            at = await admin.RunAsync(at, ct);
        }
        while (at is not null);

        return (await admin.GetRunStatusAsync(runId, ct))!;
    }

    /// <summary><see cref="RunToEndAsync"/>'s result.</summary>
    public static async Task<DeduplicationResult> RunFullAsync(
        this DeduplicationAdmin admin, CancellationToken ct = default) =>
        (await admin.RunToEndAsync(ct)).Result!;

    /// <summary>An accessor fixed on one tenant, standing in for a resolved request.</summary>
    public sealed class FixedTenantAccessor(TenantContext? context) : ITenantAccessor
    {
        public TenantContext? Context { get; } = context;
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;
        public bool IsResolved => Context is not null;

        public void SetTenant(TenantContext tenantContext) =>
            throw new NotSupportedException("The accessor is fixed in these tests.");
    }
}
