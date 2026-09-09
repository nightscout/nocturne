using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The gate every glucose write shares before it asks for an alert pass: a CGM reading is the
/// trigger each glucose alert condition is written against, so a batch carrying no positive
/// sensor value — an empty publish, or one holding only fingersticks, calibrations or
/// sensor-error sentinels — is not a trigger and must not spend an evaluation.
/// </summary>
/// <seealso cref="ICanonicalAlertEvaluator"/>
internal static class CanonicalAlertEvaluatorExtensions
{
    /// <summary>
    /// Evaluates alert rules when <paramref name="entries"/> carries at least one CGM reading.
    /// </summary>
    /// <remarks>
    /// Evaluation reads the canonical latest from storage rather than these entries, so which
    /// reading is newest does not matter here — only whether a CGM reading landed at all.
    /// </remarks>
    public static Task EvaluateForEntriesAsync(
        this ICanonicalAlertEvaluator evaluator,
        IEnumerable<Entry> entries,
        CancellationToken ct) =>
        entries.Any(e => e.Sgv is > 0) ? evaluator.EvaluateAsync(ct) : Task.CompletedTask;
}
