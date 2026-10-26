using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Platform.Releases;

/// <summary>
/// The instance's record of which release notices it has sent (<c>release_notices</c>).
/// </summary>
public interface IReleaseNoticeLedger
{
    /// <summary>
    /// Records the notice and returns true, or returns false when it was already recorded. Only
    /// the caller that gets true sends it, so replicas starting together send it once.
    /// </summary>
    Task<bool> TryClaimAsync(string kind, string version, CancellationToken cancellationToken);
}

public class ReleaseNoticeLedger(INocturneDbContextFactory contexts) : IReleaseNoticeLedger
{
    public const string Available = "available";

    public const string Installed = "installed";

    public async Task<bool> TryClaimAsync(string kind, string version, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreatePlatformAsync(cancellationToken);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO release_notices (id, kind, version)
            VALUES ({Guid.CreateVersion7()}, {kind}, {version})
            ON CONFLICT (kind, version) DO NOTHING
            """,
            cancellationToken);
        return inserted == 1;
    }
}
