using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// <see cref="IMembershipReasonLapses"/> through <see cref="MembershipReasons.RecomputeDueAsync"/>: the
/// due rows are found on a platform context, and each is recomputed on a context pinned to its tenant.
/// </summary>
public sealed class MembershipReasonLapses(INocturneDbContextFactory contexts, ILogger<MembershipReasonLapses> logger)
    : IMembershipReasonLapses
{
    /// <inheritdoc/>
    public Task<MembershipLapseSweep> NarrowLapsedAsync(DateTime now, CancellationToken ct) =>
        MembershipReasons.RecomputeDueAsync(
            contexts.CreatePlatformAsync, contexts.CreateForTenantAsync, now, logger, ct);
}
