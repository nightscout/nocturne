namespace Nocturne.Core.Contracts;

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
    Task<bool> ApproveDeviceCodeAsync(
        string userCode,
        Guid subjectId,
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
    public string DeviceCode { get; set; } = string.Empty;
    public string UserCode { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
    public int Interval { get; set; }
}

/// <summary>
/// Device code information for the approval page.
/// </summary>
public class DeviceCodeInfo
{
    public Guid Id { get; set; }
    public string UserCode { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string? ClientDisplayName { get; set; }
    public bool IsKnownClient { get; set; }
    public List<string> Scopes { get; set; } = new();
    public bool IsExpired { get; set; }
    public bool IsApproved { get; set; }
    public bool IsDenied { get; set; }
}
