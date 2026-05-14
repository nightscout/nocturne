using System.Text.Json.Serialization;

namespace Nocturne.Connectors.CareLink.Models;

public class CareLinkUserInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("accountId")]
    public long? AccountId { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("firstName")]
    public string? FirstName { get; set; }

    [JsonPropertyName("lastName")]
    public string? LastName { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;
}

public class CareLinkPatientLink
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;
}
