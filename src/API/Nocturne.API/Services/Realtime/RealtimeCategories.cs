using Nocturne.Core.Models.Authorization;

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

    /// <summary>
    /// The OAuth read scope governing each category. Security-critical: a subscriber joins a
    /// category's broadcast group only when its credential satisfies the scope listed here, and the
    /// broadcasts carry the records themselves. A category absent from this map cannot be subscribed
    /// to at all (fail-closed), so a new category must be classified here to become reachable.
    /// </summary>
    /// <remarks>
    /// The vocabulary is the OAuth read scopes in <see cref="OAuthScopes"/>. It is not the same set as
    /// <see cref="ShareDataCategories.GoverningScopes"/>, which covers only the share-reachable
    /// categories — <c>profiles</c>/<c>therapy</c> here answer to <see cref="OAuthScopes.TherapyRead"/>,
    /// which no share link can hold. <c>care</c> and <c>treatments</c> are the treatment family, so
    /// they answer to <see cref="OAuthScopes.TreatmentsRead"/> as a whole even though a BG check inside
    /// <c>care</c> is governed by glucose.read for share links.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> GoverningScopes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Entries] = OAuthScopes.GlucoseRead,
            [Treatments] = OAuthScopes.TreatmentsRead,
            [DeviceStatus] = OAuthScopes.DevicesRead,
            [Profiles] = OAuthScopes.TherapyRead,
            [Glucose] = OAuthScopes.GlucoseRead,
            [Care] = OAuthScopes.TreatmentsRead,
            [Device] = OAuthScopes.DevicesRead,
            [Therapy] = OAuthScopes.TherapyRead,
        };
}
