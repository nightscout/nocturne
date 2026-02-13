using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Tidepool.Models;

public class TidepoolAuthResponse
{
    [JsonPropertyName("emailVerified")]
    public bool EmailVerified { get; set; }

    [JsonPropertyName("emails")]
    public List<string> Emails { get; set; } = [];

    [JsonPropertyName("termsAccepted")]
    public DateTime? TermsAccepted { get; set; }

    [JsonPropertyName("userid")]
    public string Userid { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;
}
