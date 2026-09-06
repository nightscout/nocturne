using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Platform;

/// <summary>
/// In-memory singleton that tracks bot heartbeat state and derives per-channel availability.
/// The bot framework posts heartbeats containing the active platform list; this service converts
/// those heartbeats into <see cref="ChannelStatusEntry"/> records reflecting whether each
/// <see cref="ChannelType"/> is available, degraded (heartbeat stale), or unavailable (adapter not configured).
/// </summary>
/// <remarks>
/// The heartbeat only speaks for the bot's adapters, so a channel type the bot does not deliver
/// is reported available regardless of heartbeat state. Bot-backed channels are degraded if the
/// last heartbeat is older than 2 minutes (see <c>StalenessThreshold</c>).
/// </remarks>
public sealed class BotHealthService
{
    private static readonly TimeSpan StalenessThreshold = TimeSpan.FromMinutes(2);

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

        var reportedPlatforms = platforms.ToHashSet();

        var isStale = heartbeat != DateTime.MinValue
            && DateTime.UtcNow - heartbeat > StalenessThreshold;

        return Enum.GetValues<ChannelType>()
            .Select(ct =>
            {
                var entry = new ChannelStatusEntry
                {
                    ChannelType = ct,
                    Status = ChannelStatus.Available,
                    Offered = ChannelDestinations.Offered.Contains(ct),
                    RequiresDestination = ChannelDestinations.RequiresDestination(ct),
                    RequiresLink = ChannelDestinations.ResolvesFromLinkedIdentity(ct),
                };

                var platform = ChannelDestinations.PlatformOf(ct);
                if (platform is null)
                {
                    return entry;
                }

                if (!reportedPlatforms.Contains(platform))
                {
                    entry.Status = ChannelStatus.Unavailable;
                    entry.Reason = ChannelUnavailableReason.AdapterNotConfigured;
                }
                else if (isStale)
                {
                    entry.Status = ChannelStatus.Degraded;
                    entry.Reason = ChannelUnavailableReason.HeartbeatStale;
                }

                return entry;
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

    /// <summary>Whether the rule editor may add a channel of this type.</summary>
    public bool Offered { get; set; }

    /// <summary>Whether the user must supply this channel's destination for it to deliver.</summary>
    public bool RequiresDestination { get; set; }

    /// <summary>Whether this channel's destination comes from the caller's linked chat identity.</summary>
    public bool RequiresLink { get; set; }
}
