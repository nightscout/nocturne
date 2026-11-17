using System.Data.Common;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Keeps <see cref="Core.Models.V4.ApsSnapshot.ClosedLoop"/> on the Loop snapshots a write governs
/// (<see cref="IApsSnapshotRepository.StampLoopClosedLoopAsync"/>).
/// </summary>
public interface ILoopClosedLoopStamper
{
    /// <summary>Stamps the Loop snapshots among those written.</summary>
    Task AfterSnapshotsWrittenAsync(IEnumerable<ApsSnapshot> written, CancellationToken ct);

    /// <param name="starts">
    /// The starts of the Loop settings versions written, and of any settings versions moved or deleted.
    /// </param>
    /// <param name="ct">Cancels the operation.</param>
    Task AfterLoopSettingsWrittenAsync(IReadOnlyCollection<DateTime> starts, CancellationToken ct);
}

/// <summary>
/// Stamps after both writes because either can land first: Loop uploads its settings only when they
/// change, and an import writes the device statuses before the profiles.
/// </summary>
/// <remarks>
/// Runs after the write has committed and never fails it: a database error is logged and the flags
/// are left as they were. It is a system-derived write in a job scope of its own that reads as no
/// one, so a history-clamped writer still stamps the older snapshots its settings govern.
/// </remarks>
internal sealed class LoopClosedLoopStamper(
    IServiceProvider scope,
    ILogger<LoopClosedLoopStamper> logger) : ILoopClosedLoopStamper
{
    public const string AuditEndpoint = "loop-closed-loop";

    public Task AfterSnapshotsWrittenAsync(IEnumerable<ApsSnapshot> written, CancellationToken ct)
    {
        var loop = written.Where(a => a.AidAlgorithm == AidAlgorithm.Loop).Select(a => a.Timestamp).ToList();
        return loop.Count == 0
            ? Task.CompletedTask
            : RunAsync(repo => repo.StampLoopClosedLoopAsync(loop.Min(), loop.Max(), ct), ct);
    }

    public Task AfterLoopSettingsWrittenAsync(IReadOnlyCollection<DateTime> starts, CancellationToken ct) =>
        starts.Count == 0
            ? Task.CompletedTask
            : RunAsync(async repo =>
            {
                foreach (var start in starts.Distinct().Order())
                    await repo.StampLoopClosedLoopAsync(start, null, ct);
            }, ct);

    private async Task RunAsync(Func<IApsSnapshotRepository, Task> stamp, CancellationToken ct)
    {
        if (scope.GetRequiredService<ITenantAccessor>() is not { IsResolved: true, Context: { } tenant })
            return;

        try
        {
            await using var job = TenantJobScope.Open(scope, tenant, AuditEndpoint);
            await stamp(job.ServiceProvider.GetRequiredService<IApsSnapshotRepository>());
        }
        catch (DbException ex)
        {
            logger.LogWarning(ex, "Could not stamp whether the loop was closed on the Loop snapshots a write governs");
        }
    }
}
