using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Models.ClientDevices;

/// <summary>
/// Known client device kinds that can be alert-engine actuation targets. A "client device" here is
/// an OAuth-paired app install (Prelude, the desktop Companion), distinct from the telemetry
/// <c>DeviceEntity</c> that records pump/CGM hardware.
/// </summary>
public static class DeviceKinds
{
    /// <summary>The Prelude mobile app.</summary>
    public const string Prelude = "prelude";

    /// <summary>The desktop Companion app.</summary>
    public const string Companion = "companion";

    /// <summary>All recognised device kinds.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Prelude, Companion };

    /// <summary>Whether a kind string is a recognised device kind.</summary>
    public static bool IsValid(string kind) => All.Contains(kind);

    /// <summary>
    /// Whether a device kind evaluates alert rules locally with its own engine rather than relying
    /// on the cloud to push actuation intents. Local-engine devices author their actuation config in
    /// the cloud rule builder but actuate from synced rules; the server must NOT push runtime
    /// <c>device_action</c> intents to them, or it would race the device's own offline evaluation.
    /// </summary>
    public static bool HasLocalEngine(string kind) => kind == Prelude;
}

/// <summary>
/// A capability a client device can advertise and the alert engine can target.
/// </summary>
/// <param name="Key">Stable wire identifier (stored on the device, referenced by rule channels).</param>
/// <param name="Label">Factual, user-facing label for the rule builder.</param>
/// <param name="RequiredScope">OAuth scope the device's grant must hold for this capability to be honoured.</param>
/// <param name="Kinds">Device kinds that may advertise this capability.</param>
public sealed record DeviceCapability(
    string Key,
    string Label,
    string RequiredScope,
    IReadOnlySet<string> Kinds);

/// <summary>
/// Server-owned, closed registry of device actuation capabilities. Devices advertise from this set;
/// anything not listed here is dropped on registration. Each capability maps to a required OAuth
/// scope (<see cref="OAuthScopes.DeviceNotify"/> for low-risk notifications,
/// <see cref="OAuthScopes.DeviceActuate"/> for hardware) and the device kinds that may expose it.
/// </summary>
public static class DeviceCapabilities
{
    /// <summary>Show a native notification.</summary>
    public const string Notify = "notify";

    /// <summary>Vibrate / haptics.</summary>
    public const string Vibrate = "vibrate";

    /// <summary>Pulse the camera torch.</summary>
    public const string Torch = "torch";

    /// <summary>Speak the alert aloud (text-to-speech).</summary>
    public const string Speak = "speak";

    /// <summary>Take over the screen with a full-screen alert.</summary>
    public const string Fullscreen = "fullscreen";

    /// <summary>Play an alarm sound.</summary>
    public const string Sound = "sound";

    /// <summary>Flash the taskbar / tray icon.</summary>
    public const string TrayFlash = "tray_flash";

    private static IReadOnlySet<string> Kinds(params string[] kinds) => new HashSet<string>(kinds);

    /// <summary>The closed set of supported capabilities, keyed by wire identifier.</summary>
    public static readonly IReadOnlyDictionary<string, DeviceCapability> Registry =
        new Dictionary<string, DeviceCapability>
        {
            [Notify] = new(Notify, "Send a notification", OAuthScopes.DeviceNotify,
                Kinds(DeviceKinds.Prelude, DeviceKinds.Companion)),
            [Vibrate] = new(Vibrate, "Vibrate", OAuthScopes.DeviceActuate,
                Kinds(DeviceKinds.Prelude)),
            [Torch] = new(Torch, "Flash the torch", OAuthScopes.DeviceActuate,
                Kinds(DeviceKinds.Prelude)),
            [Speak] = new(Speak, "Speak the alert aloud", OAuthScopes.DeviceActuate,
                Kinds(DeviceKinds.Prelude)),
            [Fullscreen] = new(Fullscreen, "Show a full-screen alert", OAuthScopes.DeviceActuate,
                Kinds(DeviceKinds.Prelude)),
            [Sound] = new(Sound, "Play an alarm sound", OAuthScopes.DeviceActuate,
                Kinds(DeviceKinds.Prelude)),
            [TrayFlash] = new(TrayFlash, "Flash the taskbar", OAuthScopes.DeviceActuate,
                Kinds(DeviceKinds.Companion)),
        };

    /// <summary>Whether a capability string is in the registry.</summary>
    public static bool IsKnown(string capability) => Registry.ContainsKey(capability);

    /// <summary>
    /// Resolve which advertised capabilities are accepted for a device: each must be known, allowed
    /// for the device kind, and covered by the granted OAuth scopes. Unknown, kind-disallowed, or
    /// unauthorized capabilities are dropped silently (not an error) so an app can advertise a
    /// forward-looking set that older servers or narrower grants simply ignore. Order-stable.
    /// </summary>
    public static IReadOnlyList<string> Accept(
        string kind,
        IEnumerable<string> advertised,
        IReadOnlySet<string> grantedScopes)
    {
        var accepted = new List<string>();
        var seen = new HashSet<string>();
        foreach (var cap in advertised)
        {
            if (!seen.Add(cap))
                continue;
            if (!Registry.TryGetValue(cap, out var def))
                continue;
            if (!def.Kinds.Contains(kind))
                continue;
            if (!OAuthScopes.SatisfiesScope(grantedScopes, def.RequiredScope))
                continue;
            accepted.Add(cap);
        }
        return accepted;
    }

    /// <summary>
    /// Parse the requested capabilities from a <c>device_action</c> channel's metadata JSON
    /// (<c>{"capabilities": ["notify", ...]}</c>). Returns empty on null/blank/malformed input.
    /// </summary>
    public static IReadOnlyList<string> ParseRequestedCapabilities(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return [];
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("capabilities", out var caps)
                && caps.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                return caps.EnumerateArray()
                    .Where(x => x.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(x => x.GetString()!)
                    .ToList();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Malformed channel metadata — treat as no requested capabilities.
        }

        return [];
    }

    /// <summary>
    /// Project the registry into the static catalog the rule-builder consumes: the known kinds and,
    /// per capability, its label, required scope, allowed kinds, and whether it is a hardware actuator
    /// (i.e. gated on <see cref="OAuthScopes.DeviceActuate"/>).
    /// </summary>
    public static DeviceCapabilityCatalog BuildCatalog() => new()
    {
        // Push-mode kinds first: the rule builder seeds a new channel from Kinds[0], and a
        // local-engine kind (Prelude) as the default would author a channel the server
        // suppresses at runtime.
        Kinds = DeviceKinds.All
            .OrderBy(DeviceKinds.HasLocalEngine)
            .ThenBy(k => k, StringComparer.Ordinal)
            .ToList(),
        Capabilities = Registry.Values
            .Select(c => new DeviceCapabilityInfo
            {
                Key = c.Key,
                Label = c.Label,
                RequiredScope = c.RequiredScope,
                Kinds = [.. c.Kinds],
                IsHardware = c.RequiredScope == OAuthScopes.DeviceActuate,
            })
            .ToList(),
    };
}
