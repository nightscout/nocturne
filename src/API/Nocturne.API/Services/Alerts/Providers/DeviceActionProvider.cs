using Nocturne.API.Services.Realtime;
using Nocturne.Core.Models;
using Nocturne.Core.Models.ClientDevices;

namespace Nocturne.API.Services.Alerts.Providers;

/// <summary>
/// Delivers alert actuation intents to registered client devices. The channel's destination is the
/// target device KIND; metadata carries the requested capabilities.
/// </summary>
/// <remarks>
/// Local-engine kinds (e.g. Prelude) are suppressed here — they evaluate the rule and actuate from
/// their own synced config, so a runtime cloud push would race that offline evaluation. Push-mode
/// kinds (e.g. the Companion) receive a real-time <c>device_action</c> intent on the tenant's
/// authenticated DataHub group; devices treat the active-intents snapshot as authoritative and
/// reconcile from it on (re)connect, so this live push is a low-latency nudge rather than the source
/// of truth.
/// </remarks>
internal sealed class DeviceActionProvider(
    ISignalRBroadcastService broadcastService,
    ILogger<DeviceActionProvider> logger)
{
    /// <summary>
    /// Sends an actuation intent for a channel targeting <paramref name="targetKind"/>. Returns
    /// <c>true</c> when handled — either broadcast to push-mode devices, or correctly suppressed for
    /// a local-engine kind; <c>false</c> when the target kind is unknown.
    /// </summary>
    public Task<bool> SendAsync(string targetKind, AlertPayload payload, string? channelMetadata, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetKind) || !DeviceKinds.IsValid(targetKind))
        {
            logger.LogWarning("device_action channel has unknown target kind '{Kind}'; skipping.", targetKind);
            return Task.FromResult(false);
        }

        if (DeviceKinds.HasLocalEngine(targetKind))
        {
            // Suppressed by design: a local-engine device actuates from its own synced rule config.
            logger.LogDebug(
                "Suppressing device_action push to local-engine kind {Kind} for excursion {ExcursionId}.",
                targetKind, payload.ExcursionId);
            return Task.FromResult(true);
        }

        return BroadcastAsync(targetKind, payload, channelMetadata);
    }

    private async Task<bool> BroadcastAsync(string targetKind, AlertPayload payload, string? channelMetadata)
    {
        var intent = new DeviceActionIntent
        {
            Intent = "opened",
            ExcursionId = payload.ExcursionId,
            RuleName = payload.RuleName,
            Severity = payload.Severity,
            TargetKind = targetKind,
            Capabilities = [.. DeviceCapabilities.ParseRequestedCapabilities(channelMetadata)],
            Acknowledged = false,
            StartedAt = payload.ReadingTimestamp,
            GlucoseValue = payload.GlucoseValue,
            Trend = payload.Trend,
        };

        await broadcastService.BroadcastDeviceActionAsync(intent);
        return true;
    }
}
