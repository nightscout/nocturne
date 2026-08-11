using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Visibility level for tracker definitions, controlling who can view a tracker in the UI.
/// </summary>
/// <seealso cref="TrackerCategory"/>
/// <seealso cref="DashboardVisibility"/>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrackerVisibility
{
    /// <summary>
    /// Visible to everyone, including unauthenticated users (default)
    /// </summary>
    Public = 0,

    /// <summary>
    /// Only visible to the owner and admins
    /// </summary>
    Private = 1,

    /// <summary>
    /// Requires specific roles to view. Not implemented and not accepted: a tracker carries no
    /// roles to match a viewer against. Rejected on create and update, and stored rows were
    /// migrated to <see cref="Private"/>. Retained only so the wire values of
    /// <see cref="Public"/> and <see cref="Private"/> do not shift.
    /// </summary>
    RoleRestricted = 2
}
