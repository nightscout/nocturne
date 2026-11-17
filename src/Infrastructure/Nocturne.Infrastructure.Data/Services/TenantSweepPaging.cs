namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// The page size and pauses of the per-tenant paged sweeps:
/// <see cref="ApsSnapshotCurvesRewriter.RewriteTenantAsync"/> and
/// <see cref="TempBasalOriginCorroborator.CorroborateTenantAsync"/>.
/// </summary>
public static class TenantSweepPaging
{
    public const int PageSize = 500;

    /// <summary>The pause after a page that wrote, which leaves the database to live traffic.</summary>
    public static readonly TimeSpan PauseAfterWrite = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The pause after a page that wrote nothing. A page that wrote nothing is still a read, so a
    /// sweep with nothing left to write costs only a fraction of one backend on each run.
    /// </summary>
    public static readonly TimeSpan PauseAfterRead = TimeSpan.FromMilliseconds(50);
}
