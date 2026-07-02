namespace Nocturne.API.Services.Realtime;

/// <summary>
/// The realtime broadcast category names — the SignalR group tokens clients subscribe to via
/// <c>DataHub.Subscribe</c>. Single source of truth so the hub allowlist and the broadcasters agree.
/// </summary>
/// <remarks>
/// The four <see cref="V4"/> categories carry native V4 record shapes (with a <c>recordType</c>
/// discriminator) and are additive: the four legacy <see cref="V1"/> collections — which the Node
/// socket.io bridge subscribes to — keep their own broadcasts untouched. <c>care</c> is the
/// treatment-family category, named distinctly so it never collides with the v1 <c>treatments</c> group.
/// </remarks>
public static class RealtimeCategories
{
    // Legacy v1 collections (projected shapes; the socket.io bridge subscribes to these).
    public const string Entries = "entries";
    public const string Treatments = "treatments";
    public const string DeviceStatus = "devicestatus";
    public const string Profiles = "profiles";

    // Native V4 categories (record shapes + recordType discriminator).
    public const string Glucose = "glucose";
    public const string Care = "care";
    public const string Device = "device";
    public const string Therapy = "therapy";

    /// <summary>The legacy v1 collection names.</summary>
    public static readonly string[] V1 = [Entries, Treatments, DeviceStatus, Profiles];

    /// <summary>The native V4 category names.</summary>
    public static readonly string[] V4 = [Glucose, Care, Device, Therapy];

    /// <summary>Every subscribable category (v1 + V4) — the <c>DataHub.Subscribe</c> allowlist.</summary>
    public static readonly string[] All = [.. V1, .. V4];
}
