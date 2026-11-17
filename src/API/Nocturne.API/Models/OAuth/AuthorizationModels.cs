using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Models.OAuth;

/// <summary>
/// Consent approval request (submitted by the consent page)
/// </summary>
public class ConsentApprovalRequest
{
    /// <summary>
    /// OAuth client identifier requesting access.
    /// </summary>
    [FromForm(Name = "client_id")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// URI to redirect the user to after consent is granted or denied.
    /// </summary>
    [FromForm(Name = "redirect_uri")]
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// Space-delimited list of requested OAuth scopes.
    /// </summary>
    [FromForm(Name = "scope")]
    public string? Scope { get; set; }

    /// <summary>
    /// Opaque state value passed through to the redirect URI for CSRF protection.
    /// </summary>
    [FromForm(Name = "state")]
    public string? State { get; set; }

    /// <summary>
    /// PKCE code challenge for the authorization request.
    /// </summary>
    [FromForm(Name = "code_challenge")]
    public string CodeChallenge { get; set; } = string.Empty;

    /// <summary>
    /// Whether the user approved the consent request.
    /// </summary>
    [FromForm(Name = "approved")]
    public bool Approved { get; set; }

    /// <summary>
    /// When true, the grant reads only the last 24 hours of every time-series category, a rolling
    /// window from now.
    /// </summary>
    [FromForm(Name = "limit_to_24_hours")]
    public bool LimitTo24Hours { get; set; }
}

/// <summary>
/// Client info response for the consent page
/// </summary>
public class OAuthClientInfoResponse
{
    /// <summary>
    /// OAuth client identifier.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name for the client application.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Whether this client is a recognized/trusted application.
    /// </summary>
    public bool IsKnown { get; set; }

    /// <summary>
    /// URL of the client application's homepage.
    /// </summary>
    public string? Homepage { get; set; }

    /// <summary>
    /// URL of the client application's logo image.
    /// </summary>
    public string? LogoUri { get; set; }

    /// <summary>
    /// What the requested scopes allow the client to delete. Null when the request names no
    /// scope, so a caller that forgets to send it gets no answer rather than a reassuring one.
    /// </summary>
    public ScopeDeletionAccess? DeletionAccess { get; set; }
}

/// <summary>
/// Device Authorization Response (RFC 8628 Section 3.2)
/// </summary>
public class OAuthDeviceAuthorizationResponse
{
    /// <summary>
    /// Device verification code for the device to poll with.
    /// </summary>
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = string.Empty;

    /// <summary>
    /// Short code displayed to the user for entry on the verification page.
    /// </summary>
    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = string.Empty;

    /// <summary>
    /// URI where the user should navigate to enter the user code.
    /// </summary>
    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; set; } = string.Empty;

    /// <summary>
    /// Optional URI that includes the user code, allowing one-step verification.
    /// </summary>
    [JsonPropertyName("verification_uri_complete")]
    public string? VerificationUriComplete { get; set; }

    /// <summary>
    /// Lifetime of the device code in seconds.
    /// </summary>
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    /// <summary>
    /// Minimum polling interval in seconds the device should wait between token requests.
    /// </summary>
    [JsonPropertyName("interval")]
    public int Interval { get; set; }
}

/// <summary>
/// Device approval request (submitted by the device approval page)
/// </summary>
public class DeviceApprovalRequest
{
    /// <summary>
    /// The user code displayed on the device, entered by the user on the approval page.
    /// </summary>
    [FromForm(Name = "user_code")]
    public string UserCode { get; set; } = string.Empty;

    /// <summary>
    /// Whether the user approved the device authorization request.
    /// </summary>
    [FromForm(Name = "approved")]
    public bool Approved { get; set; }
}
