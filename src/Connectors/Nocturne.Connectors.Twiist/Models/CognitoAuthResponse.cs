using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Twiist.Models;

/// <summary>
/// AWS Cognito InitiateAuth response.
/// </summary>
public class CognitoAuthResponse
{
    [JsonPropertyName("AuthenticationResult")]
    public CognitoAuthResult? AuthenticationResult { get; set; }

    /// <summary>
    ///     Present instead of <see cref="AuthenticationResult"/> when Cognito wants something more
    ///     from the account (new password, MFA). Nothing here can satisfy it, so it is the only
    ///     evidence of why the connector will never authenticate.
    /// </summary>
    [JsonPropertyName("ChallengeName")]
    public string? ChallengeName { get; set; }
}

public class CognitoAuthResult
{
    [JsonPropertyName("AccessToken")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("IdToken")]
    public string? IdToken { get; set; }

    [JsonPropertyName("RefreshToken")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("ExpiresIn")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("TokenType")]
    public string? TokenType { get; set; }
}
