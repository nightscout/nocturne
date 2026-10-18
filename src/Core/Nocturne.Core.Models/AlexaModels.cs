using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Alexa Skills Kit request model
/// </summary>
public class AlexaRequest
{
    public string Version { get; set; } = "1.0";

    public AlexaSession Session { get; set; } = new();

    public AlexaRequestDetails Request { get; set; } = new();
}

public class AlexaRequestDetails
{
    /// <summary>
    /// Request type (LaunchRequest, IntentRequest, SessionEndedRequest)
    /// </summary>
    public string Type { get; set; } = string.Empty;

    public string RequestId { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; }

    public AlexaIntent? Intent { get; set; }

    public string Locale { get; set; } = "en-US";
}

/// <summary>
/// Alexa Skills Kit response model
/// </summary>
public class AlexaResponse
{
    public string Version { get; set; } = "1.0";

    public Dictionary<string, object> SessionAttributes { get; set; } = new();

    public AlexaResponseBody Response { get; set; } = new();
}

public class AlexaSession
{
    public string SessionId { get; set; } = string.Empty;

    public bool New { get; set; }

    public Dictionary<string, object> Attributes { get; set; } = new();

    public AlexaUser User { get; set; } = new();
}

public class AlexaIntent
{
    public string Name { get; set; } = string.Empty;

    public Dictionary<string, AlexaSlot> Slots { get; set; } = new();
}

public class AlexaSlot
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public class AlexaUser
{
    public string UserId { get; set; } = string.Empty;
}

public class AlexaResponseBody
{
    public AlexaOutputSpeech? OutputSpeech { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool ShouldEndSession { get; set; } = true;

    public AlexaCard? Card { get; set; }

    /// <summary>
    /// Reprompt for keeping session open
    /// </summary>
    public AlexaReprompt? Reprompt { get; set; }
}

/// <summary>
/// Alexa response details (alias for AlexaResponseBody)
/// </summary>
public class AlexaResponseDetails : AlexaResponseBody { }

public class AlexaCard
{
    /// <summary>
    /// Card type ("Simple", "Standard", "LinkAccount")
    /// </summary>
    public string Type { get; set; } = "Simple";

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Card content (for Simple cards)
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Card text (for Standard cards)
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// Small image URL (for Standard cards)
    /// </summary>
    public string? SmallImageUrl { get; set; }

    /// <summary>
    /// Large image URL (for Standard cards)
    /// </summary>
    public string? LargeImageUrl { get; set; }
}

/// <summary>
/// Alexa reprompt for keeping session open
/// </summary>
public class AlexaReprompt
{
    public AlexaOutputSpeech? OutputSpeech { get; set; }
}

public class AlexaOutputSpeech
{
    /// <summary>
    /// Speech type ("PlainText" or "SSML")
    /// </summary>
    public string Type { get; set; } = "PlainText";

    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// SSML markup (if Type is "SSML")
    /// </summary>
    public string? Ssml { get; set; }
}
