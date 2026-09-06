namespace Nocturne.API.Services.Realtime;

/// <summary>
/// The SignalR group tokens a connection joins as a consequence of the credential it presented,
/// shared by the hub that joins them and the broadcasters that publish to them.
/// </summary>
/// <remarks>
/// Distinct from <see cref="RealtimeCategories"/>, which names the per-data-category groups a client
/// opts into by calling <c>DataHub.Subscribe</c>. A category group carries one category of the
/// tenant's records; the groups here carry either the whole tenant's live payloads or one subject's.
/// </remarks>
/// <seealso cref="RealtimeCategories"/>
/// <seealso cref="Hubs.HubCredentialKind"/>
public static class RealtimeGroups
{
    /// <summary>
    /// Tenant-wide live data: the <c>dataUpdate</c>, <c>trackerUpdate</c> and <c>device_action</c>
    /// broadcasts a connection receives after <c>DataHub.Authorize</c>. Restricted to a credential
    /// that belongs to the tenant — see <c>HubAuthorization.CanJoinTenantRelay</c>.
    /// </summary>
    public const string Authorized = "authorized";

    /// <summary>Tenant administrators, for admin-only notices.</summary>
    public const string Admin = "admin";

    /// <summary>
    /// Infrastructure relay. Every per-subject payload is published here as well as to the owning
    /// subject's group, because the socket.io bridge holds a single instance-key connection per
    /// tenant and does its own per-browser fan-out. Only an
    /// <see cref="Hubs.HubCredentialKind.Infrastructure"/> credential may join, so no direct SignalR
    /// consumer — widget, tray, desktop, follower connector — receives another subject's payload.
    /// </summary>
    /// <remarks>
    /// That is the guarantee for direct SignalR consumers only. The browser app is a socket.io client
    /// of the bridge, and the bridge's own fan-out
    /// (<c>src/Web/packages/bridge/src/lib/socketio-server.ts</c>,
    /// <c>broadcastInAppNotification</c>) emits <c>notificationCreated</c>,
    /// <c>notificationUpdated</c> and <c>notificationArchived</c> to the whole tenant room with no
    /// per-subject filter, so a browser client still sees every member's in-app notifications.
    /// Closing that needs a per-subject room in the bridge.
    /// </remarks>
    public const string Relay = "relay";

    /// <summary>
    /// The group carrying one subject's own payloads: its in-app notifications and the device
    /// notification mirrors addressed to it.
    /// </summary>
    /// <param name="subjectId">The subject identifier, as carried on the payload.</param>
    /// <remarks>
    /// Normalizes the identifier because SignalR group names are compared byte for byte, and the
    /// subject arrives as a <see cref="Guid"/> on the hub side but as a string on the payloads.
    /// </remarks>
    public static string ForSubject(string subjectId) =>
        $"user-{(Guid.TryParse(subjectId, out var parsed) ? parsed.ToString("D") : subjectId)}";

    /// <inheritdoc cref="ForSubject(string)"/>
    public static string ForSubject(Guid subjectId) => $"user-{subjectId:D}";
}
