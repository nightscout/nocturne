using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// A tracker definition's visibility, retained on the wire for older clients. It restricts
/// nothing: trackers are the tenant's, and every member who may read trackers sees all of them.
/// </summary>
/// <seealso cref="TrackerCategory"/>
/// <seealso cref="DashboardVisibility"/>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrackerVisibility
{
    /// <summary>Stored value only; restricts nothing.</summary>
    Public = 0,

    /// <summary>Stored value only; restricts nothing. The default for a new definition.</summary>
    Private = 1,

    /// <summary>
    /// Never implemented and not accepted: rejected on create and update, and stored rows were
    /// migrated to <see cref="Private"/>. Retained only so the wire values of
    /// <see cref="Public"/> and <see cref="Private"/> do not shift.
    /// </summary>
    RoleRestricted = 2
}
