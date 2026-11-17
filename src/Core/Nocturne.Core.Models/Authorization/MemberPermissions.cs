namespace Nocturne.Core.Models.Authorization;

/// <summary>How a membership's roles and direct permissions combine.</summary>
public static class MemberPermissions
{
    /// <summary>The union of every role's permissions and the direct permissions.</summary>
    public static IEnumerable<string> Effective(
        IEnumerable<IEnumerable<string>> rolePermissions, IEnumerable<string>? directPermissions) =>
        rolePermissions.SelectMany(permissions => permissions).Union(directPermissions ?? []);
}
