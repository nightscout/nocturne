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
