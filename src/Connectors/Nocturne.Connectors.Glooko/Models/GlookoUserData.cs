using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Glooko.Models;

public class GlookoUserData
{
    [JsonPropertyName("userLogin")] public UserLogin UserLogin { get; set; } = new();
}

public class UserLogin
{
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;

    [JsonPropertyName("glookoCode")] public string GlookoCode { get; set; } = string.Empty;
}