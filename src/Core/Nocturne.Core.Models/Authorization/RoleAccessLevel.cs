using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// How much a role lets its members do, judged from its actual permissions rather than its name,
/// since a seeded role's permissions can be edited.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RoleAccessLevel>))]
public enum RoleAccessLevel
{
    /// <summary>Reads only, plus control of the member's own devices.</summary>
    ReadOnly,

    /// <summary>Reads, plus logging treatments and managing alerts.</summary>
    ReadAndLogTreatments,

    /// <summary>Anything more: settings, therapy, other data, or who has access.</summary>
    Manage,
}

public static class RoleAccessLevels
{
    private static readonly HashSet<string> TreatmentLogging =
        [Scope.TreatmentsReadWrite, Scope.AlertsReadWrite];

    /// <returns>Null for a role that grants nothing.</returns>
    public static RoleAccessLevel? Of(IReadOnlyCollection<string> permissions)
    {
        if (permissions.Count == 0)
            return null;
        if (permissions.All(IsRead))
            return RoleAccessLevel.ReadOnly;
        if (permissions.All(p => IsRead(p) || TreatmentLogging.Contains(p)))
            return RoleAccessLevel.ReadAndLogTreatments;
        return RoleAccessLevel.Manage;
    }

    private static bool IsRead(string permission) =>
        permission.EndsWith(".read", StringComparison.Ordinal)
        || permission is Scope.DeviceNotify or Scope.DeviceActuate;
}
