using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Authorization;

/// <summary>
/// The read side of <see cref="StateSpanWriteScopeGuard"/>: the scope that reads each state-span
/// category. A category's read scope is the read tier of its write scope, so the two maps cannot
/// drift. The list and by-id reads narrow to the categories the caller may read rather than declaring
/// one controller scope; the per-category routes declare theirs.
/// </summary>
internal static class StateSpanReadScopeGuard
{
    /// <summary>
    /// The read scope of each category. <see cref="StateSpanCategory.Profile"/> is absent from the write
    /// map because no Profile span is written any more; the spans the profile-activation conversion
    /// could not match are kept, and they are therapy data.
    /// </summary>
    public static readonly IReadOnlyDictionary<StateSpanCategory, string> CategoryReadScopes =
        StateSpanWriteScopeGuard.CategoryWriteScopes
            .ToDictionary(kvp => kvp.Key, kvp => Scope.ImpliedReadScope(kvp.Value)
                ?? throw new InvalidOperationException($"{kvp.Value} has no read tier"))
            .Append(new KeyValuePair<StateSpanCategory, string>(StateSpanCategory.Profile, Scope.TherapyRead))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

    /// <summary>
    /// The read scope of <paramref name="category"/>; an unmapped category resolves to
    /// <see cref="Scope.FullAccess"/> so it fails closed.
    /// </summary>
    public static string RequiredReadScope(StateSpanCategory category) =>
        CategoryReadScopes.TryGetValue(category, out var scope) ? scope : Scope.FullAccess;

    /// <summary>Whether <paramref name="grantedScopes"/> reads <paramref name="category"/>.</summary>
    public static bool CanRead(IReadOnlySet<string> grantedScopes, StateSpanCategory category) =>
        Scope.Satisfies(grantedScopes, RequiredReadScope(category));

    /// <summary>Every category <paramref name="grantedScopes"/> reads.</summary>
    public static IReadOnlyCollection<StateSpanCategory> ReadableCategories(IReadOnlySet<string> grantedScopes) =>
        Enum.GetValues<StateSpanCategory>().Where(c => CanRead(grantedScopes, c)).ToList();
}
