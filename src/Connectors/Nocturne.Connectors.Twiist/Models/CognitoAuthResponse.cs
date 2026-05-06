using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Twiist.Models;

/// <summary>
/// AWS Cognito InitiateAuth response.
/// </summary>
public class CognitoAuthResponse
{
    [JsonPropertyName("AuthenticationResult")]
    public CognitoAuthResult? AuthenticationResult { get; set; }
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
