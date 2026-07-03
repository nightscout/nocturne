using System.ComponentModel.DataAnnotations;

namespace Nocturne.Core.Models.ClientDevices;

/// <summary>
/// Request body for registering or refreshing a client device. The endpoint is an idempotent upsert
/// keyed on <see cref="InstallId"/> within the caller's tenant, so an app calls it on every startup.
/// </summary>
/// <seealso cref="ClientDeviceDto"/>
public class RegisterDeviceRequest
{
    /// <summary>Stable install identifier (UUID) the app generates once and persists locally.</summary>
    [Required]
    [MaxLength(64)]
    [RegularExpression("^[A-Za-z0-9_-]{1,64}$")]
    public string InstallId { get; set; } = string.Empty;

    /// <summary>Device kind (see <see cref="DeviceKinds"/>), e.g. <c>prelude</c> or <c>companion</c>.</summary>
    [Required]
    [MaxLength(32)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>User-facing label for this install (e.g. "Rhys's Pixel"). Optional.</summary>
    [MaxLength(255)]
    public string? Label { get; set; }

    /// <summary>
    /// Capabilities the device advertises. The server keeps only those known (in
    /// <see cref="DeviceCapabilities.Registry"/>), allowed for the kind, and covered by the grant's
    /// scopes; anything else is dropped, so apps may advertise a forward-looking set.
    /// </summary>
    public List<string> Capabilities { get; set; } = [];
}

/// <summary>Request body for renaming a registered device.</summary>
public class RenameDeviceRequest
{
    /// <summary>New user-facing label (null clears it).</summary>
    [MaxLength(255)]
    public string? Label { get; set; }
}

/// <summary>A registered client device as returned by the API.</summary>
/// <seealso cref="RegisterDeviceRequest"/>
public class ClientDeviceDto
{
    /// <summary>Server-assigned device id.</summary>
    public Guid Id { get; set; }

    /// <summary>The client-generated install identifier.</summary>
    public string InstallId { get; set; } = string.Empty;

    /// <summary>Device kind.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>User-facing label.</summary>
    public string? Label { get; set; }

    /// <summary>The capabilities the server accepted for this device.</summary>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>Last time the device registered or was seen online.</summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>When the device was first registered.</summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>One capability in the actuation catalog, for the rule-builder's device-action picker.</summary>
public class DeviceCapabilityInfo
{
    /// <summary>Wire identifier (e.g. <c>notify</c>, <c>torch</c>).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Factual, user-facing label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>OAuth scope the device's grant must hold (<c>device.notify</c> or <c>device.actuate</c>).</summary>
    public string RequiredScope { get; set; } = string.Empty;

    /// <summary>Device kinds that may expose this capability.</summary>
    public List<string> Kinds { get; set; } = [];

    /// <summary>Whether this is a hardware actuator (torch/vibrate/sound/full-screen) — the rule
    /// builder warns when attaching one to a non-critical rule.</summary>
    public bool IsHardware { get; set; }
}

/// <summary>
/// The static actuation catalog: the known device kinds and the capabilities each can expose.
/// Drives the rule-builder's device-action authoring UI from a single server-owned source.
/// </summary>
public class DeviceCapabilityCatalog
{
    /// <summary>Targetable device kinds (e.g. <c>companion</c>, <c>prelude</c>).</summary>
    public List<string> Kinds { get; set; } = [];

    /// <summary>The supported capabilities.</summary>
    public List<DeviceCapabilityInfo> Capabilities { get; set; } = [];
}
