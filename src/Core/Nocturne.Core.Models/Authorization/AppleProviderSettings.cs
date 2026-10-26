namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// The Apple Developer account values a Sign in with Apple provider signs its client secret with.
/// The provider's client ID is the Services ID, and its client secret is the <c>.p8</c> private key
/// in PEM form.
/// </summary>
/// <seealso cref="OidcProviderType.Apple"/>
public class AppleProviderSettings
{
    /// <summary>The 10-character Team ID of the Apple Developer account.</summary>
    public string TeamId { get; set; } = string.Empty;

    /// <summary>The 10-character ID of the Sign in with Apple private key.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>
    /// The bundle ID of the primary App ID the Services ID is grouped with. Apple can address
    /// server-to-server notifications to it rather than to the Services ID; a notification is
    /// accepted for either.
    /// </summary>
    public string? PrimaryAppId { get; set; }
}
