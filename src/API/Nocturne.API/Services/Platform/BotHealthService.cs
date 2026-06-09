using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Platform;

/// <summary>
/// In-memory singleton that tracks bot heartbeat state and derives per-channel availability.
/// The bot framework posts heartbeats containing the active platform list; this service converts
/// those heartbeats into <see cref="ChannelStatusEntry"/> records reflecting whether each
/// <see cref="ChannelType"/> is available, degraded (heartbeat stale), or unavailable (adapter not configured).
/// </summary>
/// <remarks>
/// Channels in <see cref="AlwaysAvailable"/> (WebPush, Webhook) are always reported as available
/// regardless of heartbeat state. Bot-backed channels are degraded if the last heartbeat is older
/// than 2 minutes (see <c>StalenessThreshold</c>).
/// </remarks>
public sealed class BotHealthService
{
    private static readonly TimeSpan StalenessThreshold = TimeSpan.FromMinutes(2);

    private static readonly Dictionary<string, ChannelType> PlatformToChannel = new()
    {
        ["discord"] = ChannelType.DiscordDm,
        ["slack"] = ChannelType.SlackDm,
        ["telegram"] = ChannelType.Telegram,
        ["whatsapp"] = ChannelType.WhatsApp,
        ["resend"] = ChannelType.ResendEmail,
    };

    private static readonly HashSet<ChannelType> AlwaysAvailable =
        [ChannelType.WebPush, ChannelType.Webhook];

    private static readonly HashSet<ChannelType> RequiresLinkTypes =
        [ChannelType.DiscordDm, ChannelType.SlackDm, ChannelType.Telegram, ChannelType.WhatsApp];

    private string[] _lastPlatforms = [];
    private DateTime _lastHeartbeat = DateTime.MinValue;
    private readonly object _lock = new();

    /// <summary>Records a bot heartbeat with the set of currently active platforms.</summary>
    /// <param name="platforms">Array of platform identifiers (e.g. <c>"discord"</c>, <c>"telegram"</c>) reported by the bot.</param>
    /// <param name="timestamp">Optional heartbeat timestamp; defaults to <see cref="DateTime.UtcNow"/> when not supplied.</param>
    public void Record(string[] platforms, DateTime? timestamp = null)
    {
        lock (_lock)
        {
            _lastPlatforms = platforms;
            _lastHeartbeat = timestamp ?? DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Returns the current availability status for every known <see cref="ChannelType"/>.
    /// </summary>
    /// <returns>A read-only list of <see cref="ChannelStatusEntry"/> — one entry per <see cref="ChannelType"/>.</returns>
    public IReadOnlyList<ChannelStatusEntry> GetChannelStatuses()
    {
        string[] platforms;
        DateTime heartbeat;

        lock (_lock)
        {
            platforms = _lastPlatforms;
            heartbeat = _lastHeartbeat;
        }

        var reportedChannels = platforms
            .Where(PlatformToChannel.ContainsKey)
            .Select(p => PlatformToChannel[p])
            .ToHashSet();

        var isStale = heartbeat != DateTime.MinValue
            && DateTime.UtcNow - heartbeat > StalenessThreshold;

        return Enum.GetValues<ChannelType>()
            .Select(ct =>
            {
                if (AlwaysAvailable.Contains(ct))
                {
                    return new ChannelStatusEntry
                    {
                        ChannelType = ct,
                        Status = ChannelStatus.Available,
                        RequiresLink = false,
                    };
                }

                if (!reportedChannels.Contains(ct))
                {
                    return new ChannelStatusEntry
                    {
                        ChannelType = ct,
                        Status = ChannelStatus.Unavailable,
                        Reason = ChannelUnavailableReason.AdapterNotConfigured,
                        RequiresLink = RequiresLinkTypes.Contains(ct),
                    };
                }

                if (isStale)
                {
                    return new ChannelStatusEntry
                    {
                        ChannelType = ct,
                        Status = ChannelStatus.Degraded,
                        Reason = ChannelUnavailableReason.HeartbeatStale,
                        RequiresLink = RequiresLinkTypes.Contains(ct),
                    };
                }

                return new ChannelStatusEntry
                {
                    ChannelType = ct,
                    Status = ChannelStatus.Available,
                    RequiresLink = RequiresLinkTypes.Contains(ct),
                };
            })
            .ToList();
    }
}

/// <summary>Represents the derived availability status of a single alert delivery channel.</summary>
public class ChannelStatusEntry
{
    public ChannelType ChannelType { get; set; }
    public ChannelStatus Status { get; set; }
    public ChannelUnavailableReason? Reason { get; set; }
    public bool RequiresLink { get; set; }
}
