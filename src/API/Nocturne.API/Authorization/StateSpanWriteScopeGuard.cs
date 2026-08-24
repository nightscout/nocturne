using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Authorization;

/// <summary>
/// Enforces per-record OAuth write scopes on the state-span write endpoints.
/// <c>state_spans</c> holds four different data categories behind one table and one controller,
/// and the caller chooses which by setting <see cref="StateSpan.Category"/> in the request body,
/// so a single declared controller scope would either under-gate the other three categories or
/// deny the common one. The same shape as <see cref="ActivityWriteScopeGuard"/>: the controller
/// applies this before delegating to the service.
/// </summary>
/// <remarks>
/// The category that matters most here is <see cref="StateSpanCategory.DataExclusion"/>. Excluding
/// a window marks glucose readings as not to be counted, so it changes what analytics and reports
/// show — it is a glucose-integrity write, not a treatment annotation, and gating it on the
/// treatments scope would let a treatments-only credential hide a hypo from every report.
/// <para>
/// Connector publishing writes state spans through <c>IStateSpanService</c> directly rather than
/// through this controller, so it is not gated here — the same carve-out
/// <see cref="ActivityWriteScopeGuard"/> documents.
/// </para>
/// </remarks>
/// <seealso cref="ShareDataCategories"/>
internal static class StateSpanWriteScopeGuard
{
    /// <summary>
    /// The write scope each state-span category belongs to. Chosen from the category the record's
    /// content belongs to, matching what the equivalent V1/V3 endpoint requires:
    /// <list type="bullet">
    /// <item><see cref="StateSpanCategory.PumpMode"/> and
    /// <see cref="StateSpanCategory.PumpConnectivity"/> describe the pump, so they are the devices
    /// category — the same category <c>ReservoirReportsController</c> and the pump-snapshot
    /// surface use.</item>
    /// <item><see cref="StateSpanCategory.Profile"/> is a profile switch, which V1/V3 profile
    /// writes gate on the therapy scope.</item>
    /// <item><see cref="StateSpanCategory.DataExclusion"/> governs which glucose readings count,
    /// so it is the glucose category.</item>
    /// <item><see cref="StateSpanCategory.Override"/>, <see cref="StateSpanCategory.Exercise"/>,
    /// <see cref="StateSpanCategory.Illness"/>, <see cref="StateSpanCategory.Travel"/> and
    /// <see cref="StateSpanCategory.TemporaryTarget"/> are the decomposed form of the legacy
    /// treatment events, which V1 <c>ActivityController</c> and V3 <c>TreatmentsController</c>
    /// both gate on the treatments scope.</item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlyDictionary<StateSpanCategory, string> CategoryWriteScopes =
        new Dictionary<StateSpanCategory, string>
        {
            [StateSpanCategory.PumpMode] = Scope.DevicesReadWrite,
            [StateSpanCategory.PumpConnectivity] = Scope.DevicesReadWrite,
            [StateSpanCategory.Profile] = Scope.TherapyReadWrite,
            [StateSpanCategory.DataExclusion] = Scope.GlucoseReadWrite,
            [StateSpanCategory.Override] = Scope.TreatmentsReadWrite,
            [StateSpanCategory.Exercise] = Scope.TreatmentsReadWrite,
            [StateSpanCategory.Illness] = Scope.TreatmentsReadWrite,
            [StateSpanCategory.Travel] = Scope.TreatmentsReadWrite,
            [StateSpanCategory.TemporaryTarget] = Scope.TreatmentsReadWrite,
        };

    /// <summary>
    /// Returns the write scope required for <paramref name="category"/>. An unmapped category —
    /// one added to the enum without being classified here — resolves to
    /// <see cref="Scope.FullAccess"/> so a new category fails closed rather than inheriting
    /// whichever scope happened to be listed first.
    /// </summary>
    public static string RequiredWriteScope(StateSpanCategory category) =>
        CategoryWriteScopes.TryGetValue(category, out var scope) ? scope : Scope.FullAccess;

    /// <summary>
    /// Returns the first write scope the caller is missing across
    /// <paramref name="categories"/>, or <see langword="null"/> when all are satisfied. An update
    /// that moves a span between categories passes both, so the caller needs write access to the
    /// category it is leaving as well as the one it is entering.
    /// </summary>
    /// <param name="grantedScopes">The caller's resolved granted scopes.</param>
    /// <param name="categories">The categories the write touches.</param>
    public static string? FindMissingScope(
        IReadOnlySet<string> grantedScopes,
        params StateSpanCategory[] categories)
    {
        foreach (var scope in categories.Select(RequiredWriteScope).Distinct())
        {
            if (!Scope.Satisfies(grantedScopes, scope))
                return scope;
        }

        return null;
    }
}
