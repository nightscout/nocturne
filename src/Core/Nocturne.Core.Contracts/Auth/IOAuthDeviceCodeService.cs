namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Service for managing OAuth Device Authorization Grant (RFC 8628) device codes.
/// </summary>
public interface IOAuthDeviceCodeService
{
    /// <summary>
    /// Create a device code pair (device_code + user_code) for a client.
    /// </summary>
    Task<DeviceCodeResult> CreateDeviceCodeAsync(
        string clientId,
        IEnumerable<string> scopes,
        CancellationToken ct = default
    );

    /// <summary>
    /// Look up a device code by user_code for the approval page.
    /// </summary>
    Task<DeviceCodeInfo?> GetByUserCodeAsync(
        string userCode,
        CancellationToken ct = default
    );

    /// <summary>
    /// Approve a device code (user approved on the approval page).
    /// Creates a grant and links it to the device code entity.
    /// </summary>
    /// <param name="userCode">The code the user typed.</param>
    /// <param name="subjectId">The approving subject.</param>
    /// <param name="limitTo24Hours">Whether the grant may read only the last 24 hours.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> ApproveDeviceCodeAsync(
        string userCode,
        Guid subjectId,
        bool limitTo24Hours = false,
        CancellationToken ct = default
    );

    /// <summary>
    /// Deny a device code (user denied on the approval page).
    /// </summary>
    Task<bool> DenyDeviceCodeAsync(
        string userCode,
        CancellationToken ct = default
    );
}

/// <summary>
/// Result of creating a device code pair.
/// </summary>
public class DeviceCodeResult
{
    /// <summary>The device verification code, passed to the token endpoint by the device.</summary>
    public string DeviceCode { get; set; } = string.Empty;

    /// <summary>The short user code displayed to the user for entry on the approval page.</summary>
    public string UserCode { get; set; } = string.Empty;

    /// <summary>Seconds until the device code expires.</summary>
    public int ExpiresIn { get; set; }

    /// <summary>Minimum polling interval in seconds the device must observe between token requests.</summary>
    public int Interval { get; set; }
}

/// <summary>
/// Device code information for the approval page.
/// </summary>
public class DeviceCodeInfo
{
    /// <summary>Internal entity ID of the device code record.</summary>
    public Guid Id { get; set; }

    /// <summary>The short user code displayed to the user.</summary>
    public string UserCode { get; set; } = string.Empty;

    /// <summary>The OAuth client_id that initiated the device authorization request.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Display name of the requesting client, if available.</summary>
    public string? ClientDisplayName { get; set; }

    /// <summary>Whether the client comes from the bundled known-app directory.</summary>
    public bool IsKnownClient { get; set; }

    /// <summary>Scopes requested by the device.</summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary>Whether the device code has passed its expiry time.</summary>
    public bool IsExpired { get; set; }

    /// <summary>Whether a user has approved the device code on the approval page.</summary>
    public bool IsApproved { get; set; }

    /// <summary>Whether a user has denied the device code on the approval page.</summary>
    public bool IsDenied { get; set; }
}
