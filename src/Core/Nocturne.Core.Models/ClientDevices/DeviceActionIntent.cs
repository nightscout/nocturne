using Nocturne.Core.Models.Alerts;

namespace Nocturne.Core.Models.ClientDevices;

/// <summary>
/// An actuation intent the alert engine sends to push-mode client devices (the desktop Companion,
/// and any future no-local-engine kind). Delivered live over SignalR on alert open, and also served
/// by the active-intents snapshot so a device that was offline can reconcile on reconnect.
/// </summary>
/// <remarks>
/// The device is the source of *how* to actuate: it honours the intersection of <see cref="Capabilities"/>
/// (what the rule requested) with its own granted capabilities, and respects its local quiet hours.
/// <see cref="ExcursionId"/> is the stable dedup/correlation key across the live event and the snapshot.
/// </remarks>
public class DeviceActionIntent
{
    /// <summary>Lifecycle phase: <c>opened</c>, <c>resolved</c>, or <c>acknowledged</c>.</summary>
    public string Intent { get; set; } = "opened";

    /// <summary>The excursion this intent belongs to — the stable correlation/dedup key.</summary>
    public Guid ExcursionId { get; set; }

    /// <summary>Display name of the rule that fired.</summary>
    public string RuleName { get; set; } = string.Empty;

    /// <summary>Severity of the firing rule (drives notification treatment on the device).</summary>
    public AlertRuleSeverity Severity { get; set; } = AlertRuleSeverity.Warning;

    /// <summary>The device kind this intent targets (e.g. <c>companion</c>).</summary>
    public string TargetKind { get; set; } = string.Empty;

    /// <summary>Capabilities the rule requested. The device actuates the subset it actually has.</summary>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>Whether the excursion has been acknowledged (on any device) — the device should not re-alarm.</summary>
    public bool Acknowledged { get; set; }

    /// <summary>When the excursion started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// Current glucose value (mg/dL) at fire time. Best-effort and live-only: populated on the
    /// real-time push, but null in the reconcile snapshot (which carries no point-in-time reading).
    /// Devices must tolerate it being absent.
    /// </summary>
    public decimal? GlucoseValue { get; set; }

    /// <summary>Glucose trend direction string. Live-only, like <see cref="GlucoseValue"/>; null in snapshots.</summary>
    public string? Trend { get; set; }
}
