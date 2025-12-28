using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Visibility level for tracker definitions
/// </summary>
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
    /// Requires specific roles to view (future extension)
    /// </summary>
    RoleRestricted = 2
}
