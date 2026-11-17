using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Platform;

/// <summary>
/// Service for handling Alexa Skills Kit requests.
/// Provides voice assistant integration for Nightscout data.
/// </summary>
/// <seealso cref="AlexaRequest"/>
/// <seealso cref="AlexaResponse"/>
/// <seealso cref="AlexaIntent"/>
public interface IAlexaService
{
    /// <summary>
    /// Process an Alexa request and generate appropriate response
    /// Handles LaunchRequest, IntentRequest, and SessionEndedRequest types
    /// </summary>
    /// <returns>Alexa response with speech output, or null for a session-ended request, which is answered with an empty string</returns>
    Task<AlexaResponse?> ProcessRequestAsync(
        AlexaRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Handle launch request when user opens the skill without specific intent
    /// </summary>
    /// <param name="locale">User's locale for localized responses</param>
    /// <returns>Welcome response for the skill</returns>
    Task<AlexaResponse> HandleLaunchRequestAsync(string locale);

    /// <summary>
    /// Handle intent request with specific user intent and slots
    /// </summary>
    /// <param name="intent">The intent to process</param>
    /// <param name="locale">User's locale for localized responses</param>
    /// <returns>Response based on the intent</returns>
    Task<AlexaResponse> HandleIntentRequestAsync(
        AlexaIntent intent,
        string locale,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// The answer for a request that failed, one wording for every error path.
    /// </summary>
    AlexaResponse BuildErrorResponse();
}
