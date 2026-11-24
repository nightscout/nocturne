namespace Nocturne.API.Extensions;

/// <summary>
/// What a caller may give anyone else (<see cref="HttpContextExtensions.CallerGrantCeiling"/>).
/// </summary>
/// <param name="Scopes">The scopes the caller may confer.</param>
/// <param name="AccessEndsAt">When the caller's access to give ends, in UTC; null when it does not.</param>
/// <param name="HistoryClamped">Whether what the caller gives must read only the last 24 hours.</param>
public sealed record GrantCeiling(IReadOnlySet<string> Scopes, DateTime? AccessEndsAt, bool HistoryClamped)
{
    /// <summary>
    /// The ceiling of a caller with no direct reason, for a read that shows what the caller may give;
    /// a write refuses such a caller outright. It confers no scope, so every flag of what the caller
    /// may give reads false. Its end is none, so a read that marks what outlasts the caller marks
    /// nothing: <c>RoleController.GetRoles</c> shows no role as held past such a caller.
    /// </summary>
    public static readonly GrantCeiling Nothing = new(new HashSet<string>(), null, true);
}
