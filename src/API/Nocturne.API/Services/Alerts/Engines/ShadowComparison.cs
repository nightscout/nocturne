using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>
/// One rule's shadow-mode comparisons and their <c>AlertEngineDivergence</c> log line, shared
/// by <see cref="ShadowAlertEngine"/> and <see cref="ShadowExcursionDecider"/>.
/// </summary>
/// <param name="logger">Where divergences are logged.</param>
/// <param name="engine">The secondary engine's name in the log line.</param>
/// <param name="ruleId">The rule compared.</param>
internal sealed class ShadowComparison(ILogger logger, string engine, Guid ruleId)
{
    /// <summary>
    /// The shadow run pins its own "now" just before the managed run starts. The managed
    /// evaluators read the clock mid-evaluation, so freshly set instants differ by however long
    /// the managed pass took. Anything inside this window is clock skew.
    /// </summary>
    private static readonly TimeSpan SkewTolerance = TimeSpan.FromSeconds(5);

    public void Diverged(string field, object managed, object shadow) =>
        logger.LogWarning(
            "AlertEngineDivergence rule={RuleId} engine={Engine} field={Field} managed={Managed} rust={Rust}",
            ruleId, engine, field, managed, shadow);

    /// <summary>
    /// Compares two decisions made from the same state and instant, so every instant must match
    /// exactly. Fields are prefixed with <paramref name="operation"/>.
    /// </summary>
    public void Decisions(string operation, TrackerDecision managed, TrackerDecision shadow)
    {
        Transition(operation, managed.Type, managed.CloseReason, shadow.Type, shadow.CloseReason);
        PostState(operation, managed.Post, shadow.Post, exactInstants: true);
    }

    public void Transition(
        string operation,
        ExcursionTransitionType managedType,
        ExcursionCloseReason? managedReason,
        ExcursionTransitionType shadowType,
        ExcursionCloseReason? shadowReason)
    {
        if (managedType != shadowType)
            Diverged($"{operation}.transition",
                RustEnvelopeMapper.TransitionToWire(managedType), RustEnvelopeMapper.TransitionToWire(shadowType));
        if (managedReason != shadowReason)
            Diverged($"{operation}.close_reason",
                RustEnvelopeMapper.CloseReasonToWire(managedReason) ?? "(none)",
                RustEnvelopeMapper.CloseReasonToWire(shadowReason) ?? "(none)");
    }

    /// <summary>
    /// Compares post-states, field names prefixed with <paramref name="operation"/>. Instants match
    /// exactly when <paramref name="exactInstants"/> (both sides decided at one instant), otherwise
    /// within the clock-skew tolerance.
    /// </summary>
    public void PostState(string operation, TrackerPostState? managed, TrackerPostState? shadow, bool exactInstants)
    {
        if (!string.Equals(managed?.State, shadow?.State, StringComparison.Ordinal))
            Diverged($"{operation}.tracker_state", managed?.State ?? "(none)", shadow?.State ?? "(none)");

        if ((managed?.ConfirmationCount ?? 0) != (shadow?.ConfirmationCount ?? 0))
            Diverged($"{operation}.confirmation_count", managed?.ConfirmationCount ?? 0, shadow?.ConfirmationCount ?? 0);

        if ((managed?.HasExcursion ?? false) != (shadow?.HasExcursion ?? false))
            Diverged($"{operation}.active_excursion", managed?.HasExcursion ?? false, shadow?.HasExcursion ?? false);

        if ((managed?.AwaitingRearm ?? false) != (shadow?.AwaitingRearm ?? false))
            Diverged($"{operation}.awaiting_rearm", managed?.AwaitingRearm ?? false, shadow?.AwaitingRearm ?? false);

        if (!InstantsEqual(managed?.HysteresisStartedAt, shadow?.HysteresisStartedAt, exactInstants))
            Diverged($"{operation}.hysteresis_started_at",
                Format(managed?.HysteresisStartedAt), Format(shadow?.HysteresisStartedAt));

        if (!InstantsEqual(managed?.UpdatedAt, shadow?.UpdatedAt, exactInstants))
            Diverged($"{operation}.updated_at", Format(managed?.UpdatedAt), Format(shadow?.UpdatedAt));
    }

    public void Timers(
        string field, IReadOnlyDictionary<string, DateTime> managed, IReadOnlyDictionary<string, DateTime> shadow)
    {
        if (!TimersEqual(managed, shadow))
            Diverged(field, FormatTimers(managed), FormatTimers(shadow));
    }

    private static bool TimersEqual(
        IReadOnlyDictionary<string, DateTime> managed,
        IReadOnlyDictionary<string, DateTime> shadow)
    {
        if (managed.Count != shadow.Count) return false;
        foreach (var (path, at) in managed)
        {
            if (!shadow.TryGetValue(path, out var other)) return false;
            if (!InstantsEqual(at, other, exact: false)) return false;
        }
        return true;
    }

    private static string FormatTimers(IReadOnlyDictionary<string, DateTime> timers) =>
        timers.Count == 0
            ? "(empty)"
            : string.Join(";", timers.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value:O}"));

    private static bool InstantsEqual(DateTime? managed, DateTime? shadow, bool exact) =>
        (managed, shadow) switch
        {
            (null, null) => true,
            ({ } m, { } s) => exact ? m == s : (s - m).Duration() <= SkewTolerance,
            _ => false,
        };

    private static string Format(DateTime? at) => at is { } v ? v.ToString("O") : "(none)";
}
