using Microsoft.Extensions.Caching.Memory;
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
/// <para>
/// A per-(tenant, kind) rate limit caps how many live nudges are broadcast per window, as a backstop
/// against a runaway/flapping producer flooding the hub. It deliberately throttles ONLY the live
/// nudge, never the active-intents snapshot — a real (safety) alert is never hidden; a throttled
/// device simply learns it on its next periodic reconcile instead of instantly.
/// </para>
/// </remarks>
internal sealed class DeviceActionProvider(
    ISignalRBroadcastService broadcastService,
    IMemoryCache cache,
    ILogger<DeviceActionProvider> logger)
{
    /// <summary>Max live device_action nudges broadcast per (tenant, kind) per <see cref="RateWindow"/>.</summary>
    internal const int MaxNudgesPerWindow = 30;

    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Sends an actuation intent for a channel targeting <paramref name="targetKind"/>. Returns
    /// <c>true</c> when handled — either broadcast to push-mode devices, or correctly suppressed for
    /// a local-engine kind; <c>false</c> when the target kind is unknown.
    /// </summary>
    public Task<bool> SendAsync(string targetKind, AlertPayload payload, string? channelMetadata, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetKind) || !DeviceKinds.IsValid(targetKind))
        {
            // The only raw user string logged in this path; strip newlines to keep log records intact.
            var sanitizedKind = targetKind?.ReplaceLineEndings(" ");
            logger.LogWarning("device_action channel has unknown target kind '{Kind}'; skipping.", sanitizedKind);
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
        if (!WithinRateLimit(payload.TenantId, targetKind))
        {
            // Backstop only: suppress the live nudge. The active-intents snapshot is untouched, so
            // connected devices still pick this excursion up on their next reconcile — the alert is
            // throttled, not lost.
            logger.LogWarning(
                "device_action nudge rate limit hit for tenant {TenantId} kind {Kind}; suppressing the live push (snapshot still delivers on reconcile).",
                payload.TenantId, targetKind);
            return true;
        }

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

    /// <summary>
    /// Fixed-window per-(tenant, kind) counter. Best-effort (the increment is not strictly atomic,
    /// which is fine for an abuse backstop). Returns false once the window's nudge budget is spent.
    /// </summary>
    private bool WithinRateLimit(Guid tenantId, string kind)
    {
        var bucket = DateTime.UtcNow.Ticks / RateWindow.Ticks;
        var key = $"device_action_rl:{tenantId}:{kind}:{bucket}";
        var count = cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = RateWindow;
            return 0;
        });

        count++;
        cache.Set(key, count, RateWindow);
        return count <= MaxNudgesPerWindow;
    }
}
