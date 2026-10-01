using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.ClientDevices;

/// <summary>
/// Stages the removal of a grant's registered devices.
/// </summary>
internal static class ClientDeviceGrantCascade
{
    /// <summary>
    /// Stages removal of every device registered under <paramref name="grantId"/> and returns how
    /// many were staged. The removals commit with the caller's next save.
    /// </summary>
    /// <remarks>
    /// A revoke only stamps the grant's <c>RevokedAt</c>, so the FK cascade that fires on a hard
    /// grant delete never runs and the devices would otherwise keep receiving actuation fan-out.
    /// </remarks>
    internal static async Task<int> RemoveGrantDevicesAsync(
        this NocturneDbContext db, Guid grantId, CancellationToken ct = default)
    {
        var devices = await db.ClientDevices
            .Where(d => d.GrantId == grantId)
            .ToListAsync(ct);

        db.ClientDevices.RemoveRange(devices);
        return devices.Count;
    }
}
