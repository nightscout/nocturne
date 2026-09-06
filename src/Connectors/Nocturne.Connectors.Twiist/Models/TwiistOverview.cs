using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Twiist.Models;

/// <summary>
/// An entry from the Twiist follower overviews endpoint (/pwd/overviews): one PWD (person with
/// diabetes) the authenticated follower account can see, with the id used by /pwd/{id}/package.
/// </summary>
public class TwiistOverview
{
    [JsonPropertyName("pwdId")]
    public string? PwdId { get; set; }

    [JsonPropertyName("pwdNickname")]
    public string? PwdNickname { get; set; }
}
