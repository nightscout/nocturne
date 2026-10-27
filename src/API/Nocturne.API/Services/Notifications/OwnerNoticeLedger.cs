using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Notifications;

/// <summary>
/// The instance's record of which one-time notices it has sent to which owner (<c>owner_notices</c>).
/// </summary>
public interface IOwnerNoticeLedger
{
    /// <summary>
    /// Records the notice for one owner and returns true, or returns false when it was already
    /// recorded. Only the caller that gets true sends it, so replicas starting together send it once.
    /// </summary>
    Task<bool> TryClaimAsync(string kind, string version, Guid tenantId, Guid subjectId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a claim whose notice was not sent, so the next check sends it.
    /// </summary>
    Task ReleaseAsync(string kind, string version, Guid tenantId, Guid subjectId, CancellationToken cancellationToken);
}

public class OwnerNoticeLedger(INocturneDbContextFactory contexts) : IOwnerNoticeLedger
{
    public async Task<bool> TryClaimAsync(
        string kind, string version, Guid tenantId, Guid subjectId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreatePlatformAsync(cancellationToken);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO owner_notices (id, kind, version, tenant_id, subject_id)
            VALUES ({Guid.CreateVersion7()}, {kind}, {version}, {tenantId}, {subjectId})
            ON CONFLICT (kind, version, tenant_id, subject_id) DO NOTHING
            """,
            cancellationToken);
        return inserted == 1;
    }

    public async Task ReleaseAsync(
        string kind, string version, Guid tenantId, Guid subjectId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreatePlatformAsync(cancellationToken);
        await db.OwnerNotices
            .Where(n => n.Kind == kind && n.Version == version && n.TenantId == tenantId && n.SubjectId == subjectId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
