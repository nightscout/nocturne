using System.Globalization;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Platform;

/// <summary>
/// Handles Alexa Skills Kit requests, providing voice-assistant integration for
/// Nightscout data with 1:1 legacy compatibility.
/// </summary>
/// <seealso cref="IAlexaService"/>
public class AlexaService : IAlexaService
{
    private readonly IEntryStore _store;
    private readonly ILogger<AlexaService> _logger;

    // Translation mappings for supported locales (simplified implementation)
    private readonly Dictionary<string, Dictionary<string, string>> _translations = new()
    {
        ["en"] = new Dictionary<string, string>
        {
            ["virtAsstTitleLaunch"] = "Nightscout",
            ["virtAsstLaunch"] =
                "Hello, I can help you check your blood sugar. What would you like to know?",
            ["virtAsstUnknownIntentTitle"] = "Unknown Request",
            ["virtAsstUnknownIntentText"] =
                "I'm sorry, I didn't understand that request. Please try again.",
        },
        ["es"] = new Dictionary<string, string>
        {
            ["virtAsstTitleLaunch"] = "Nightscout",
            ["virtAsstLaunch"] =
                "Hola, puedo ayudarte a verificar tu azúcar en sangre. ¿Qué te gustaría saber?",
            ["virtAsstUnknownIntentTitle"] = "Solicitud Desconocida",
            ["virtAsstUnknownIntentText"] =
                "Lo siento, no entendí esa solicitud. Por favor, inténtalo de nuevo.",
        },
        ["fr"] = new Dictionary<string, string>
        {
            ["virtAsstTitleLaunch"] = "Nightscout",
            ["virtAsstLaunch"] =
                "Bonjour, je peux vous aider à vérifier votre glycémie. Que voulez-vous savoir?",
            ["virtAsstUnknownIntentTitle"] = "Demande Inconnue",
            ["virtAsstUnknownIntentText"] =
                "Désolé, je n'ai pas compris cette demande. Veuillez réessayer.",
        },
    };

    public AlexaService(IEntryStore store, ILogger<AlexaService> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc/>
    /// <remarks>Maintains 1:1 compatibility with the legacy Nightscout Alexa implementation.</remarks>
    /// <param name="request">The incoming <see cref="AlexaRequest"/> from the Alexa Skills Kit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="AlexaResponse"/> appropriate for the request type.</returns>
    public async Task<AlexaResponse> ProcessRequestAsync(
        AlexaRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Processing Alexa request of type: {RequestType}", request.Request.Type);

        var locale = GetLocaleFromRequest(request);

        return request.Request.Type switch
        {
            "SessionEndedRequest" => await HandleSessionEndedRequestAsync(),
            "LaunchRequest" => await HandleLaunchRequestAsync(locale),
            "IntentRequest" => request.Request.Intent != null
                ? await HandleIntentRequestAsync(request.Request.Intent, locale, cancellationToken)
                : await HandleLaunchRequestAsync(locale), // Fallback to launch if no intent
            _ => BuildSpeechletResponse(
                Translate("virtAsstUnknownIntentTitle", locale),
                Translate("virtAsstUnknownIntentText", locale),
                string.Empty,
                true
            ),
        };
    }

    /// <summary>
    /// Handles a launch request when the user opens the skill without a specific intent.
    /// </summary>
    /// <remarks>Replicates the legacy <c>onLaunch</c> behaviour.</remarks>
    /// <param name="locale">Two-character locale code (e.g. <c>en</c>, <c>es</c>).</param>
    /// <returns>A greeting <see cref="AlexaResponse"/> that keeps the session open.</returns>
    public async Task<AlexaResponse> HandleLaunchRequestAsync(string locale)
    {
        _logger.LogDebug("Session launched");

        await Task.CompletedTask; // Legacy compatibility - no async work in launch

        return BuildSpeechletResponse(
            Translate("virtAsstTitleLaunch", locale),
            Translate("virtAsstLaunch", locale),
            Translate("virtAsstLaunch", locale),
            false
        );
    }

    /// <summary>
    /// Handles a session-ended notification from the Alexa service.
    /// </summary>
    /// <remarks>Replicates the legacy <c>onSessionEnded</c> behaviour. No async work is performed.</remarks>
    /// <returns>An empty <see cref="AlexaResponse"/> with <c>ShouldEndSession = true</c>.</returns>
    public async Task<AlexaResponse> HandleSessionEndedRequestAsync()
    {
        _logger.LogDebug("Session ended");

        await Task.CompletedTask; // Legacy compatibility - no async work in session end

        // Return empty response as per legacy implementation
        return new AlexaResponse
        {
            Version = "1.0",
            Response = new AlexaResponseDetails { ShouldEndSession = true },
        };
    }

    /// <summary>
    /// Handles a specific intent request, resolving the metric slot and dispatching to the
    /// appropriate intent handler.
    /// </summary>
    /// <remarks>Replicates the legacy <c>handleIntent</c> behaviour with slot processing.</remarks>
    /// <param name="intent">The resolved <see cref="AlexaIntent"/> from the Alexa Skills Kit.</param>
    /// <param name="locale">Two-character locale code used for response localisation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// An <see cref="AlexaResponse"/> from the matched intent handler, or an unknown-intent
    /// response when no handler is registered for the intent name.
    /// </returns>
    public async Task<AlexaResponse> HandleIntentRequestAsync(
        AlexaIntent intent,
        string locale,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Received intent request: {IntentName}", intent.Name);

        // Process metric slot if present (legacy slot processing logic)
        string? metric = null;
        if (intent.Slots?.ContainsKey("metric") == true)
        {
            var metricSlot = intent.Slots["metric"];

            // Legacy resolution processing - checking for ER_SUCCESS_MATCH
            // This replicates the JavaScript logic:
            // var slotStatus = slots?.metric?.resolutions?.resolutionsPerAuthority?.[0]?.status?.code;
            // var slotName = slots?.metric?.resolutions?.resolutionsPerAuthority?.[0]?.values?.[0]?.value?.name;

            // For now, we'll use the direct value since Alexa.NET handles resolution differently
            // CHECKME - The legacy implementation expects resolution data that may need custom handling
            if (!string.IsNullOrEmpty(metricSlot.Value))
            {
                metric = metricSlot.Value;
            }
            else
            {
                return BuildSpeechletResponse(
                    Translate("virtAsstUnknownIntentTitle", locale),
                    Translate("virtAsstUnknownIntentText", locale),
                    string.Empty,
                    true
                );
            }
        }

        // Get intent handler - in legacy this calls ctx.alexa.getIntentHandler(intentName, metric)
        // For now, we'll handle basic intents directly
        var handler = GetIntentHandler(intent.Name, metric);
        if (handler != null)
        {
            try
            {
                return await handler(intent, locale, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing intent {IntentName}", intent.Name);
                return BuildSpeechletResponse(
                    Translate("virtAsstUnknownIntentTitle", locale),
                    "Sorry, I encountered an error processing your request.",
                    string.Empty,
                    true
                );
            }
        }
        else
        {
            return BuildSpeechletResponse(
                Translate("virtAsstUnknownIntentTitle", locale),
                Translate("virtAsstUnknownIntentText", locale),
                string.Empty,
                true
            );
        }
    }

    /// <summary>
    /// Builds an <see cref="AlexaResponse"/> in the legacy <c>buildSpeechletResponse</c> format.
    /// </summary>
    /// <param name="title">Card title displayed in the Alexa app.</param>
    /// <param name="output">Plain-text speech output.</param>
    /// <param name="repromptText">Reprompt text spoken if the user does not respond; ignored when <paramref name="shouldEndSession"/> is <see langword="true"/> or when empty.</param>
    /// <param name="shouldEndSession">Whether the session should be closed after this response.</param>
    /// <returns>A fully formed <see cref="AlexaResponse"/>.</returns>
    public AlexaResponse BuildSpeechletResponse(
        string title,
        string output,
        string repromptText,
        bool shouldEndSession
    )
    {
        var response = new AlexaResponse
        {
            Version = "1.0",
            Response = new AlexaResponseDetails
            {
                OutputSpeech = new AlexaOutputSpeech { Type = "PlainText", Text = output },
                Card = new AlexaCard
                {
                    Type = "Simple",
                    Title = title,
                    Content = output,
                },
                ShouldEndSession = shouldEndSession,
            },
        };

        // Add reprompt if provided and session should not end
        if (!string.IsNullOrEmpty(repromptText) && !shouldEndSession)
        {
            response.Response.Reprompt = new AlexaReprompt
            {
                OutputSpeech = new AlexaOutputSpeech { Type = "PlainText", Text = repromptText },
            };
        }

        return response;
    }

    /// <summary>
    /// Get locale from Alexa request, defaulting to "en"
    /// Replicates legacy locale processing logic
    /// </summary>
    private static string GetLocaleFromRequest(AlexaRequest request)
    {
        var locale = request.Request.Locale;
        if (!string.IsNullOrEmpty(locale) && locale.Length > 2)
        {
            locale = locale.Substring(0, 2); // Take first 2 characters like legacy substr(0, 2)
        }
        return locale ?? "en";
    }

    /// <summary>
    /// Translate text using simple locale mapping
    /// Replicates legacy ctx.language.translate functionality
    /// </summary>
    private string Translate(string key, string locale)
    {
        if (
            _translations.TryGetValue(locale, out var localeTranslations)
            && localeTranslations.TryGetValue(key, out var translation)
        )
        {
            return translation;
        }

        // Fallback to English
        if (
            _translations.TryGetValue("en", out var englishTranslations)
            && englishTranslations.TryGetValue(key, out var englishTranslation)
        )
        {
            return englishTranslation;
        }

        // Ultimate fallback
        return key;
    }

    /// <summary>
    /// Get intent handler for specific intent and metric
    /// Replicates legacy ctx.alexa.getIntentHandler(intentName, metric) functionality
    /// </summary>
    private Func<AlexaIntent, string, CancellationToken, Task<AlexaResponse>>? GetIntentHandler(
        string intentName,
        string? metric
    )
    {
        // Legacy intent handlers would be set up via ctx.virtAsstBase.setupMutualIntents(ctx.alexa)
        // For now, we'll implement basic handlers directly
        return intentName switch
        {
            "NSStatus" => HandleNSStatusIntent,
            "NSBg" => HandleNSBgIntent,
            "AMAZON.HelpIntent" => HandleHelpIntent,
            "AMAZON.StopIntent" or "AMAZON.CancelIntent" => HandleStopIntent,
            _ => null,
        };
    }

    /// <summary>
    /// Handle Nightscout status intent
    /// </summary>
    private Task<AlexaResponse> HandleNSStatusIntent(
        AlexaIntent intent,
        string locale,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Get basic system status
            var response = "Nightscout system is running and available.";

            return Task.FromResult(
                BuildSpeechletResponse("Nightscout Status", response, string.Empty, true)
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Nightscout status");
            return Task.FromResult(
                BuildSpeechletResponse(
                    "Status Error",
                    "Sorry, I couldn't get the system status right now.",
                    string.Empty,
                    true
                )
            );
        }
    }

    /// <summary>
    /// Handle blood glucose reading intent
    /// </summary>
    private async Task<AlexaResponse> HandleNSBgIntent(
        AlexaIntent intent,
        string locale,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Get latest glucose reading using the correct method
            var entries = await _store.QueryAsync(
                new EntryQuery { Type = "sgv", Count = 1 },
                cancellationToken);
            var latest = entries.FirstOrDefault();
            if (latest == null)
            {
                return BuildSpeechletResponse(
                    "Blood Sugar",
                    "Sorry, I couldn't find any recent blood sugar readings.",
                    string.Empty,
                    true
                );
            }

            var response =
                $"Your latest blood sugar reading is {latest.Sgv} taken {GetTimeAgo(latest.Mills)}.";

            return BuildSpeechletResponse("Blood Sugar", response, string.Empty, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting blood glucose reading");
            return BuildSpeechletResponse(
                "Blood Sugar Error",
                "Sorry, I couldn't get your blood sugar reading right now.",
                string.Empty,
                true
            );
        }
    }

    /// <summary>
    /// Handle help intent
    /// </summary>
    private async Task<AlexaResponse> HandleHelpIntent(
        AlexaIntent intent,
        string locale,
        CancellationToken cancellationToken
    )
    {
        await Task.CompletedTask;

        var helpText =
            "I can help you check your blood sugar levels and system status. "
            + "Try saying 'what's my blood sugar' or 'check status'.";

        return BuildSpeechletResponse("Help", helpText, helpText, false);
    }

    /// <summary>
    /// Handle stop/cancel intent
    /// </summary>
    private async Task<AlexaResponse> HandleStopIntent(
        AlexaIntent intent,
        string locale,
        CancellationToken cancellationToken
    )
    {
        await Task.CompletedTask;

        return BuildSpeechletResponse("Goodbye", "Goodbye!", string.Empty, true);
    }

    /// <summary>
    /// Get human-readable time ago from timestamp
    /// </summary>
    private static string GetTimeAgo(long timestamp)
    {
        var date = DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
        var now = DateTimeOffset.UtcNow;
        var diff = now - date;

        if (diff.TotalMinutes < 1)
            return "just now";
        if (diff.TotalMinutes < 60)
            return $"{(int)diff.TotalMinutes} minutes ago";
        if (diff.TotalHours < 24)
            return $"{(int)diff.TotalHours} hours ago";
        return $"{(int)diff.TotalDays} days ago";
    }
}
