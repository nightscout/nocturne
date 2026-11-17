using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Alerts.Corpus;

/// <summary>The corpus's wire forms of the tracker's transition types and close reasons.</summary>
public static class CorpusWire
{
    /// <summary>A transition type as the corpus records it.</summary>
    public static string TransitionWire(ExcursionTransitionType type) => type switch
    {
        ExcursionTransitionType.None => "none",
        ExcursionTransitionType.ExcursionOpened => "opened",
        ExcursionTransitionType.ExcursionContinues => "continues",
        ExcursionTransitionType.HysteresisStarted => "hysteresis_started",
        ExcursionTransitionType.HysteresisResumed => "hysteresis_resumed",
        ExcursionTransitionType.ExcursionClosed => "closed",
        _ => throw new InvalidOperationException($"Unknown transition type {type}"),
    };

    /// <summary>A close reason as the corpus records it.</summary>
    public static string CloseReasonWire(ExcursionCloseReason reason) => reason switch
    {
        ExcursionCloseReason.Hysteresis => "hysteresis",
        ExcursionCloseReason.AutoResolve => "auto",
        ExcursionCloseReason.Manual => "manual",
        ExcursionCloseReason.RuleDisabled => "rule-disabled",
        _ => throw new InvalidOperationException($"Unknown close reason {reason}"),
    };
}
