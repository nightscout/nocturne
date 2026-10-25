using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// How far a grant's tokens are known to belong to the app its OAuth client names.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OAuthClientIdentity>))]
public enum OAuthClientIdentity
{
    /// <summary>The client is not in the known-app directory; its name is the registrant's own.</summary>
    SelfRegistered,

    /// <summary>
    /// The client is in the known-app directory, but a device-code approval added to the grant; see
    /// <c>OAuthGrantEntity.ApprovedByDeviceCode</c>.
    /// </summary>
    Unverified,

    /// <summary>The client is in the known-app directory and only redirect-bound consent reached the grant.</summary>
    Verified,
}
