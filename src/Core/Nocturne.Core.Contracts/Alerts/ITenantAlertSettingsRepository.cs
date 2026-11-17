using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Stores the tenant's scheduled Do Not Disturb settings with the manual mute that is saved
/// alongside them. Every method runs as the scope's reader on its tenant.
/// </summary>
public interface ITenantAlertSettingsRepository
{
    /// <summary>The stored settings, which are created with the defaults when the tenant has none.</summary>
    Task<TenantAlertSettingsSnapshot> GetOrCreateAsync(CancellationToken ct);

    /// <summary>
    /// Stores <paramref name="settings"/> and, when <paramref name="manual"/> is given, applies the
    /// toggle through <see cref="IDndWindowRepository.EnsureManualAsync"/> or
    /// <see cref="IDndWindowRepository.ClearManualAsync"/>, all in one save. A null toggle leaves
    /// the manual mute alone.
    /// </summary>
    Task UpdateAsync(TenantAlertSettingsSnapshot settings, ManualDndToggle? manual, CancellationToken ct);
}
