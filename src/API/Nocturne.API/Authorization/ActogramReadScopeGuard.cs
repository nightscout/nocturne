using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Authorization;

/// <summary>
/// Enforces per-category OAuth read scopes on the actogram report, the read-side sibling of
/// <see cref="ActivityReadScopeGuard"/>. <c>IActogramReportService</c> merges four storages into
/// one payload — glucose, heart rates, step counts and sleep sessions — and each dedicated storage
/// has its own read scope, so a single <c>RequireScope</c> attribute on the action can only decide
/// admission, not what the response may contain. The attribute therefore lists
/// <see cref="AdmissionScopes"/> as an OR and this guard empties every category the caller does not
/// hold.
/// </summary>
internal static class ActogramReadScopeGuard
{
    /// <summary>
    /// The read scopes that admit a caller to the actogram: holding any one of them means at least
    /// one category in the response is visible. Attribute arguments must be compile-time constants,
    /// so the admission attribute repeats these constants inline.
    /// </summary>
    public static readonly IReadOnlyList<string> AdmissionScopes =
    [
        Scope.GlucoseRead,
        Scope.HeartRateRead,
        Scope.StepCountRead,
        Scope.SleepRead,
    ];

    /// <summary>
    /// Empties every category of <paramref name="data"/> whose read scope the caller does not hold.
    /// </summary>
    /// <param name="data">The report about to be returned.</param>
    /// <param name="grantedScopes">The caller's normalized granted scopes.</param>
    public static ActogramReportData Redact(
        ActogramReportData data,
        IReadOnlySet<string> grantedScopes)
    {
        // Thresholds are the band edges of the glucose series, so they leave with it.
        if (!Scope.Satisfies(grantedScopes, Scope.GlucoseRead))
        {
            data.Glucose = [];
            data.Thresholds = new ChartThresholdsDto();
        }

        if (!Scope.Satisfies(grantedScopes, Scope.HeartRateRead))
            data.HeartRates = [];

        if (!Scope.Satisfies(grantedScopes, Scope.StepCountRead))
            data.StepCounts = [];

        if (!Scope.Satisfies(grantedScopes, Scope.SleepRead))
            data.SleepSpans = [];

        return data;
    }
}
