using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Glooko.Xt;

/// <summary>Body of the password step; the answer carries no token, only an id and <c>success</c>.</summary>
public sealed class GlookoXtPatientLoginRequest
{
    [JsonPropertyName("email")] public string Email { get; init; } = string.Empty;
    [JsonPropertyName("password")] public string Password { get; init; } = string.Empty;
}

public sealed class GlookoXtPatientLoginResponse
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(GlookoXtLenientLongConverter))]
    public long? Id { get; init; }

    [JsonPropertyName("success")] public string? Success { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>Body of the code step: the email again plus the digits the patient received.</summary>
public sealed class GlookoXtCodeAuthRequest
{
    [JsonPropertyName("email")] public string Email { get; init; } = string.Empty;
    [JsonPropertyName("code")] public string Code { get; init; } = string.Empty;
}

public sealed class GlookoXtCodeAuthResponse
{
    [JsonPropertyName("token")] public string? Token { get; init; }
    [JsonPropertyName("role")] public string? Role { get; init; }
    [JsonPropertyName("success")] public string? Success { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}
