using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Services.Platform;

/// <summary>
/// Handles Alexa Skills Kit requests, providing voice-assistant integration for
/// Nightscout data with 1:1 legacy compatibility.
/// </summary>
/// <seealso cref="IAlexaService"/>
public class AlexaService : IAlexaService
{
    private const string SuccessfulMatch = "ER_SUCCESS_MATCH";
    private const long RecentLoopWindowMills = 6 * 60 * 60 * 1000;
    private static readonly long RecentPumpWindowMills =
        new PumpPreferences().UrgentClock * 2 * 60 * 1000L;

    private readonly IEntryStore _store;
    private readonly IPropertiesService _properties;
    private readonly IDDataService _ddata;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ITenantAppearanceReader _appearanceReader;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AlexaService> _logger;
    private readonly Dictionary<string, MetricAnswer> _metricAnswers;
    private readonly MetricAnswer _fullStatus;
    private readonly MetricAnswer _lastLoop;

    // Values for es and fr are Nightscout's own; a key missing from a locale falls back to en.
    private readonly Dictionary<string, Dictionary<string, string>> _translations = new()
    {
        ["en"] = new Dictionary<string, string>
        {
            ["virtAsstTitleLaunch"] = "Welcome to Nightscout",
            ["virtAsstLaunch"] = "What would you like to check on Nightscout?",
            ["virtAsstUnknownIntentTitle"] = "Unknown Intent",
            ["virtAsstUnknownIntentText"] = "I'm sorry, I don't know what you're asking for.",
            ["FortyFiveDown"] = "slightly dropping",
            ["FortyFiveUp"] = "slightly rising",
            ["Flat"] = "holding",
            ["SingleUp"] = "rising",
            ["SingleDown"] = "dropping",
            ["DoubleDown"] = "rapidly dropping",
            ["DoubleUp"] = "rapidly rising",
            ["virtAsstUnknown"] =
                "That value is unknown at the moment. Please see your Nightscout site for more details.",
            ["virtAsstTitleAR2Forecast"] = "AR2 Forecast",
            ["virtAsstTitleCurrentBasal"] = "Current Basal",
            ["virtAsstTitleCurrentCOB"] = "Current COB",
            ["virtAsstTitleCurrentIOB"] = "Current IOB",
            ["virtAsstTitleLastLoop"] = "Last Loop",
            ["virtAsstTitlePumpReservoir"] = "Insulin Remaining",
            ["virtAsstTitlePumpBattery"] = "Pump Battery",
            ["virtAsstTitleUploaderBattery"] = "Uploader Battery",
            ["virtAsstTitleCurrentBG"] = "Current BG",
            ["virtAsstTitleDelta"] = "Blood Glucose Delta",
            ["virtAsstDelta"] = "Your delta was %1 between %2 and %3.",
            ["virtAsstDeltaEstimated"] = "Your estimated delta was %1 between %2 and %3.",
            ["virtAsstTitleFullStatus"] = "Full Status",
            ["virtAsstStatus"] = "%1 and %2 as of %3.",
            ["virtAsstBasal"] = "%1 current basal is %2 units per hour",
            ["virtAsstBasalTemp"] = "%1 temp basal of %2 units per hour will end %3",
            ["virtAsstIob"] = "and you have %1 insulin on board.",
            ["virtAsstIobIntent"] = "You have %1 insulin on board",
            ["virtAsstIobUnits"] = "%1 units of",
            ["virtAsstPreamble"] = "Your",
            ["virtAsstPreamble3person"] = "%1 has a ",
            ["virtAsstNoInsulin"] = "no",
            ["virtAsstReservoir"] = "You have %1 units remaining",
            ["virtAsstPumpBattery"] = "Your pump battery is at %1 %2",
            ["virtAsstUploaderBattery"] = "Your uploader battery is at %1",
            ["virtAsstLastLoop"] = "The last successful loop was %1",
            ["virtAsstAR2ForecastAround"] =
                "According to the AR2 forecast you are expected to be around %1 over the next %2",
            ["virtAsstAR2ForecastBetween"] =
                "According to the AR2 forecast you are expected to be between %1 and %2 over the next %3",
            ["virtAsstCob3person"] = "%1 has %2 carbohydrates on board",
            ["virtAsstCob"] = "You have %1 carbohydrates on board",
        },
        ["es"] = new Dictionary<string, string>
        {
            ["virtAsstTitleLaunch"] = "Bienvenido a Nightscout",
            ["virtAsstLaunch"] = "¿Qué quieres saber de Nightscout?",
            ["virtAsstUnknownIntentTitle"] = "Intento desconocido",
            ["virtAsstUnknownIntentText"] = "Lo siento, no sé lo que estás pidiendo.",
        },
        ["fr"] = new Dictionary<string, string>
        {
            ["virtAsstTitleLaunch"] = "Bienvenue dans Nightscout",
            ["virtAsstLaunch"] = "Qu'est ce que vous voulez voir sur Nightscout?",
            ["virtAsstUnknownIntentTitle"] = "Intentions inconnues",
            ["virtAsstUnknownIntentText"] =
                "« Je suis désolée, je ne sais pas ce que vous demandez. ».",
        },
    };

    public AlexaService(
        IEntryStore store,
        IPropertiesService properties,
        IDDataService ddata,
        IHttpContextAccessor httpContextAccessor,
        ITenantAppearanceReader appearanceReader,
        ITenantAccessor tenantAccessor,
        TimeProvider timeProvider,
        ILogger<AlexaService> logger
    )
    {
        _store = store;
        _properties = properties;
        _ddata = ddata;
        _httpContextAccessor = httpContextAccessor;
        _appearanceReader = appearanceReader;
        _tenantAccessor = tenantAccessor;
        _timeProvider = timeProvider;
        _logger = logger;
        _metricAnswers = BuildMetricAnswers();
        _fullStatus = new MetricAnswer(
            "virtAsstTitleFullStatus",
            [Scope.GlucoseRead],
            SpeakFullStatusAsync
        );
        _lastLoop = new MetricAnswer(
            "virtAsstTitleLastLoop",
            [Scope.DevicesRead],
            SpeakLastLoopAsync
        );
    }

    /// <inheritdoc/>
    /// <remarks>Maintains 1:1 compatibility with the legacy Nightscout Alexa implementation.</remarks>
    /// <param name="request">The incoming <see cref="AlexaRequest"/> from the Alexa Skills Kit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// An <see cref="AlexaResponse"/> appropriate for the request type, or null for a session-ended
    /// request, which Nightscout answers with an empty string.
    /// </returns>
    public async Task<AlexaResponse?> ProcessRequestAsync(
        AlexaRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Processing Alexa request of type: {RequestType}", request.Request.Type);

        var locale = GetLocaleFromRequest(request);

        return request.Request.Type switch
        {
            "SessionEndedRequest" => LogSessionEnded(),
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

    private AlexaResponse? LogSessionEnded()
    {
        _logger.LogDebug("Session ended");
        return null;
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

        string? metric = null;
        if (intent.Slots?.TryGetValue("metric", out var metricSlot) == true)
        {
            metric = ResolveMetric(metricSlot);
            if (metric == null)
            {
                return BuildSpeechletResponse(
                    Translate("virtAsstUnknownIntentTitle", locale),
                    Translate("virtAsstUnknownIntentText", locale),
                    string.Empty,
                    true
                );
            }
        }

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
                return BuildErrorResponse();
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

    /// <inheritdoc/>
    /// <remarks>Nightscout has no error answer, so the wording is English whatever the locale.</remarks>
    public AlexaResponse BuildErrorResponse() =>
        BuildSpeechletResponse(
            "Error",
            "Sorry, I'm having trouble right now. Please try again later.",
            string.Empty,
            true
        );

    /// <summary>
    /// Builds an <see cref="AlexaResponse"/> in the legacy <c>buildSpeechletResponse</c> format.
    /// </summary>
    /// <param name="title">Card title displayed in the Alexa app.</param>
    /// <param name="output">Plain-text speech output.</param>
    /// <param name="repromptText">Reprompt text spoken if the user does not respond. Always sent, empty when there is none, as Nightscout does.</param>
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
                Reprompt = new AlexaReprompt
                {
                    OutputSpeech = new AlexaOutputSpeech { Type = "PlainText", Text = repromptText },
                },
                ShouldEndSession = shouldEndSession,
            },
        };

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
    /// Translate text using simple locale mapping, substituting <c>%1</c>, <c>%2</c>... with
    /// <paramref name="args"/> as legacy <c>ctx.language.translate</c> does
    /// </summary>
    private string Translate(string key, string locale, params object[] args)
    {
        var text = LookupTranslation(key, locale);
        return args.Length == 0
            ? text
            : Regex.Replace(
                text,
                @"%(\d+)",
                match =>
                {
                    var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - 1;
                    return index < args.Length
                        ? Convert.ToString(args[index], CultureInfo.InvariantCulture) ?? string.Empty
                        : match.Value;
                }
            );
    }

    private string LookupTranslation(string key, string locale)
    {
        if (
            _translations.TryGetValue(locale, out var localeTranslations)
            && localeTranslations.TryGetValue(key, out var translation)
        )
        {
            return translation;
        }

        if (
            _translations.TryGetValue("en", out var englishTranslations)
            && englishTranslations.TryGetValue(key, out var englishTranslation)
        )
        {
            return englishTranslation;
        }

        return key;
    }

    /// <summary>
    /// The metric a <c>metric</c> slot names: the canonical name of an <c>ER_SUCCESS_MATCH</c>
    /// resolution, else the raw utterance. Null when the slot carries neither.
    /// </summary>
    private static string? ResolveMetric(AlexaSlot slot)
    {
        var authority = slot.Resolutions?.ResolutionsPerAuthority.FirstOrDefault();
        var name = authority?.Values.FirstOrDefault()?.Value?.Name;
        if (authority?.Status?.Code == SuccessfulMatch && !string.IsNullOrEmpty(name))
        {
            return name;
        }

        return string.IsNullOrEmpty(slot.Value) ? null : slot.Value;
    }

    private Func<AlexaIntent, string, CancellationToken, Task<AlexaResponse>>? GetIntentHandler(
        string intentName,
        string? metric
    )
    {
        return intentName switch
        {
            "NSStatus" => (_, locale, ct) => AnswerAsync(_fullStatus, locale, null, ct),
            "LastLoop" => (_, locale, ct) => AnswerAsync(_lastLoop, locale, null, ct),
            "MetricNow" when metric != null && _metricAnswers.TryGetValue(metric, out var answer) =>
                (intent, locale, ct) => AnswerAsync(answer, locale, PwdOf(intent), ct),
            "AMAZON.HelpIntent" => HandleHelpIntent,
            "AMAZON.StopIntent" or "AMAZON.CancelIntent" => HandleStopIntent,
            _ => null,
        };
    }

    private async Task<AlexaResponse> AnswerAsync(
        MetricAnswer answer,
        string locale,
        string? pwd,
        CancellationToken cancellationToken
    )
    {
        var speech = CanRead(answer.Scopes)
            ? await answer.Speak(
                new SpeechRequest(locale, _timeProvider.GetUtcNow(), pwd, cancellationToken)
            )
            : null;

        return BuildSpeechletResponse(
            Translate(answer.TitleKey, locale),
            speech ?? Translate("virtAsstUnknown", locale),
            string.Empty,
            true
        );
    }

    private bool CanRead(IEnumerable<string> scopes) =>
        _httpContextAccessor.HttpContext is { } context && scopes.All(context.HasScope);

    private static string? PwdOf(AlexaIntent intent) =>
        intent.Slots?.TryGetValue("pwd", out var slot) == true && !string.IsNullOrEmpty(slot.Value)
            ? slot.Value
            : null;

    private sealed record SpeechRequest(
        string Locale,
        DateTimeOffset Now,
        string? Pwd,
        CancellationToken CancellationToken
    );

    private sealed record MetricAnswer(
        string TitleKey,
        string[] Scopes,
        Func<SpeechRequest, Task<string?>> Speak
    );

    private static readonly string[] BasalScopes = [Scope.TreatmentsRead, Scope.TherapyRead];

    private Dictionary<string, MetricAnswer> BuildMetricAnswers()
    {
        var bg = new MetricAnswer("virtAsstTitleCurrentBG", [Scope.GlucoseRead], SpeakBgAsync);
        var iob = new MetricAnswer("virtAsstTitleCurrentIOB", [Scope.TreatmentsRead], SpeakIobAsync);
        var cob = new MetricAnswer("virtAsstTitleCurrentCOB", [Scope.TreatmentsRead], SpeakCobAsync);
        var basal = new MetricAnswer("virtAsstTitleCurrentBasal", BasalScopes, SpeakBasalAsync);
        var reservoir = new MetricAnswer(
            "virtAsstTitlePumpReservoir",
            [Scope.DevicesRead],
            SpeakReservoirAsync
        );
        var pumpBattery = new MetricAnswer(
            "virtAsstTitlePumpBattery",
            [Scope.DevicesRead],
            SpeakPumpBatteryAsync
        );
        var uploaderBattery = new MetricAnswer(
            "virtAsstTitleUploaderBattery",
            [Scope.DevicesRead],
            SpeakUploaderBatteryAsync
        );
        var ar2 = new MetricAnswer("virtAsstTitleAR2Forecast", [Scope.GlucoseRead], SpeakAr2Async);
        var delta = new MetricAnswer("virtAsstTitleDelta", [Scope.GlucoseRead], SpeakDeltaAsync);

        return new Dictionary<string, MetricAnswer>(StringComparer.OrdinalIgnoreCase)
        {
            ["bg"] = bg,
            ["blood glucose"] = bg,
            ["number"] = bg,
            ["iob"] = iob,
            ["insulin on board"] = iob,
            ["cob"] = cob,
            ["carbs on board"] = cob,
            ["carbohydrates on board"] = cob,
            ["basal"] = basal,
            ["current basal"] = basal,
            ["pump reservoir"] = reservoir,
            ["pump battery"] = pumpBattery,
            ["uploader battery"] = uploaderBattery,
            ["ar2 forecast"] = ar2,
            ["forecast"] = ar2,
            ["delta"] = delta,
            ["bg delta"] = delta,
            ["blood sugar delta"] = delta,
            ["blood glucose delta"] = delta,
        };
    }

    private async Task<string?> SpeakBgAsync(SpeechRequest request)
    {
        var entries = await _store.QueryAsync(
            new EntryQuery { Type = "sgv", Count = 1 },
            request.CancellationToken
        );
        var latest = entries.FirstOrDefault();
        if (latest == null)
        {
            return null;
        }

        return Translate(
            "virtAsstStatus",
            request.Locale,
            GlucoseUnits.Scale(
                latest.Sgv ?? latest.Mgdl,
                await GlucoseUnits.UsesMmolAsync(
                    _appearanceReader,
                    _tenantAccessor,
                    request.CancellationToken
                )
            ),
            Translate(latest.Direction ?? "NONE", request.Locale),
            MomentRelativeTime.From(
                DateTimeOffset.FromUnixTimeMilliseconds(latest.Mills),
                request.Now
            )
        );
    }

    private async Task<string?> SpeakFullStatusAsync(SpeechRequest request)
    {
        var parts = new List<(int Priority, string Speech)>();

        if (await SpeakBgAsync(request) is { } bg)
        {
            parts.Add((-1, bg));
        }

        var wantsBasal = CanRead(BasalScopes);
        var wantsIob = CanRead([Scope.TreatmentsRead]);
        if (wantsBasal || wantsIob)
        {
            var properties = await _properties.GetPropertiesAsync(
                ["basal", "iob"],
                request.CancellationToken
            );
            // NIGHTSCOUT-COMPAT: Nightscout speaks "basal is NaN units per hour" and "0 units of insulin" here; Nocturne omits the basal sentence and says "no insulin" for zero.
            var basal = wantsBasal ? BasalSentence(properties, request) : null;
            var iobUnits = wantsIob ? IobUnits(properties, request.Locale) : null;

            // virtAsstBasal has no full stop: Nightscout always follows it with the IOB clause.
            if (basal is not null)
            {
                parts.Add((1, iobUnits is null ? basal + "." : basal));
            }

            if (iobUnits is not null)
            {
                parts.Add(
                    (
                        2,
                        basal is not null
                            ? Translate("virtAsstIob", request.Locale, iobUnits)
                            : Translate("virtAsstIobIntent", request.Locale, iobUnits) + "."
                    )
                );
            }
        }

        return parts.Count == 0
            ? null
            : string.Join(' ', parts.OrderBy(part => part.Priority).Select(part => part.Speech));
    }

    private async Task<string?> SpeakIobAsync(SpeechRequest request)
    {
        var properties = await _properties.GetPropertiesAsync(["iob"], request.CancellationToken);
        return IobUnits(properties, request.Locale) is { } units
            ? Translate("virtAsstIobIntent", request.Locale, units)
            : null;
    }

    private string? IobUnits(Dictionary<string, object> properties, string locale)
    {
        if (NumberOf(properties, "iob", "iob") is not { } iob)
        {
            return null;
        }

        return iob == 0
            ? Translate("virtAsstNoInsulin", locale)
            : Translate(
                "virtAsstIobUnits",
                locale,
                iob.ToString("F2", CultureInfo.InvariantCulture)
            );
    }

    private async Task<string?> SpeakCobAsync(SpeechRequest request)
    {
        var properties = await _properties.GetPropertiesAsync(["cob"], request.CancellationToken);
        var cob = NumberOf(properties, "cob", "cob") ?? 0;

        return request.Pwd is { } pwd
            ? Translate("virtAsstCob3person", request.Locale, pwd.Replace("'s", string.Empty), cob)
            : Translate("virtAsstCob", request.Locale, cob);
    }

    private async Task<string?> SpeakBasalAsync(SpeechRequest request) =>
        BasalSentence(
            await _properties.GetPropertiesAsync(["basal"], request.CancellationToken),
            request
        );

    private string? BasalSentence(Dictionary<string, object> properties, SpeechRequest request)
    {
        if (
            !properties.TryGetValue("basal", out var basal)
            || basal is not Dictionary<string, object> basalProperties
            || basalProperties["current"] is not Dictionary<string, object> current
        )
        {
            return null;
        }

        var preamble = request.Pwd is { } pwd
            ? Translate("virtAsstPreamble3person", request.Locale, pwd)
            : Translate("virtAsstPreamble", request.Locale);
        var totalBasal = Convert.ToDouble(current["totalbasal"], CultureInfo.InvariantCulture);

        if (current["treatment"] is Treatment { Duration: { } duration } tempBasal)
        {
            var end = DateTimeOffset.FromUnixTimeMilliseconds(
                tempBasal.Mills + (long)(duration * 60_000)
            );
            return Translate(
                "virtAsstBasalTemp",
                request.Locale,
                preamble,
                totalBasal,
                MomentRelativeTime.From(end, request.Now)
            );
        }

        return Translate("virtAsstBasal", request.Locale, preamble, totalBasal);
    }

    private async Task<string?> SpeakReservoirAsync(SpeechRequest request)
    {
        var reservoir = (await RecentPumpStatusesAsync(request))
            .Select(status => status.Pump?.Reservoir)
            .FirstOrDefault(value => value != null);

        return reservoir is { } units
            ? Translate("virtAsstReservoir", request.Locale, units)
            : null;
    }

    private async Task<string?> SpeakPumpBatteryAsync(SpeechRequest request)
    {
        var battery = (await RecentPumpStatusesAsync(request))
            .Select(status => status.Pump?.Battery)
            .FirstOrDefault(value => value?.Percent != null || value?.Voltage != null);

        if (battery?.Percent is { } percent)
        {
            return Translate("virtAsstPumpBattery", request.Locale, percent, "percent");
        }

        return battery?.Voltage is { } volts
            ? Translate("virtAsstPumpBattery", request.Locale, volts, "volts")
            : null;
    }

    private async Task<string?> SpeakUploaderBatteryAsync(SpeechRequest request)
    {
        var properties = await _properties.GetPropertiesAsync(["upbat"], request.CancellationToken);
        return
            properties.TryGetValue("upbat", out var upbat)
            && upbat is Dictionary<string, object> battery
            && battery["display"] is string display
            ? Translate("virtAsstUploaderBattery", request.Locale, display)
            : null;
    }

    private async Task<string?> SpeakAr2Async(SpeechRequest request)
    {
        var properties = await _properties.GetPropertiesAsync(["ar2"], request.CancellationToken);
        if (
            !properties.TryGetValue("ar2", out var ar2)
            || ar2 is not Dictionary<string, object> ar2Properties
            || !ar2Properties.TryGetValue("forecast", out var forecast)
            || forecast is not Dictionary<string, object> forecastProperties
        )
        {
            return null;
        }

        var predicted = JsonSerializer
            .SerializeToElement(forecastProperties["predicted"])
            .EnumerateArray()
            .Select(point => (
                Mgdl: point.GetProperty("mgdl").GetDouble(),
                Mills: point.GetProperty("mills").GetInt64()
            ))
            .ToList();
        if (predicted.Count == 0)
        {
            return null;
        }

        var min = Math.Round(predicted.Min(point => point.Mgdl));
        var max = Math.Round(predicted.Max(point => point.Mgdl));
        var horizon = MomentRelativeTime.From(
            DateTimeOffset.FromUnixTimeMilliseconds(predicted.Max(point => point.Mills)),
            request.Now
        );

        return min == max
            ? Translate("virtAsstAR2ForecastAround", request.Locale, max, horizon)
            : Translate("virtAsstAR2ForecastBetween", request.Locale, min, max, horizon);
    }

    private async Task<string?> SpeakLastLoopAsync(SpeechRequest request)
    {
        var now = request.Now.ToUnixTimeMilliseconds();
        var lastLoop = (await LatestDeviceStatusesAsync(request))
            .Where(status => status.Mills >= now - RecentLoopWindowMills && status.Mills <= now)
            .SelectMany(LoopMoments)
            .Max();

        return lastLoop is { } mills
            ? Translate(
                "virtAsstLastLoop",
                request.Locale,
                MomentRelativeTime.From(DateTimeOffset.FromUnixTimeMilliseconds(mills), request.Now)
            )
            : null;
    }

    private static IEnumerable<long?> LoopMoments(DeviceStatus status)
    {
        if (
            status.Loop is { } loop
            && string.IsNullOrEmpty(loop.FailureReason)
            && UploaderTimestamp.TryParse(loop.Timestamp, out var loopTime)
        )
        {
            yield return loopTime.ToUnixTimeMilliseconds();
        }

        if (status.OpenAps is { } openAps)
        {
            if (openAps.Enacted is { } enacted && (enacted.Received == true || enacted.Recieved == true))
            {
                yield return CommandMoment(enacted);
            }

            if (openAps.Suggested is { } suggested)
            {
                yield return CommandMoment(suggested);
            }
        }
    }

    private static long? CommandMoment(OpenApsSuggested command) =>
        command.Mills
        ?? (
            UploaderTimestamp.TryParse(command.Timestamp, out var time)
                ? time.ToUnixTimeMilliseconds()
                : null
        );

    private async Task<string?> SpeakDeltaAsync(SpeechRequest request)
    {
        var properties = await _properties.GetPropertiesAsync(["delta"], request.CancellationToken);
        if (
            !properties.TryGetValue("delta", out var value)
            || value is not Dictionary<string, object> delta
            || delta["times"] is not Dictionary<string, long> times
        )
        {
            return null;
        }

        var mgdl = Convert.ToDouble(delta["mgdl"], CultureInfo.InvariantCulture);
        var useMmol = await GlucoseUnits.UsesMmolAsync(
            _appearanceReader,
            _tenantAccessor,
            request.CancellationToken
        );
        var display = useMmol
            ? $"{(mgdl >= 0 ? "+" : string.Empty)}{GlucoseUnits.Scale(mgdl, true).ToString("F1", CultureInfo.InvariantCulture)}"
            : (string)delta["display"];

        return Translate(
            delta["interpolated"] is true ? "virtAsstDeltaEstimated" : "virtAsstDelta",
            request.Locale,
            display == "+0" ? "0" : display,
            MomentRelativeTime.From(DateTimeOffset.FromUnixTimeMilliseconds(times["recent"]), request.Now),
            MomentRelativeTime.From(DateTimeOffset.FromUnixTimeMilliseconds(times["previous"]), request.Now)
        );
    }

    private async Task<IEnumerable<DeviceStatus>> RecentPumpStatusesAsync(SpeechRequest request)
    {
        var now = request.Now.ToUnixTimeMilliseconds();
        return (await LatestDeviceStatusesAsync(request)).Where(status =>
            status.Pump != null
            && status.Mills >= now - RecentPumpWindowMills
            && status.Mills <= now
        );
    }

    private async Task<IEnumerable<DeviceStatus>> LatestDeviceStatusesAsync(SpeechRequest request)
    {
        var ddata = await _ddata.GetDDataAsync(
            request.Now.ToUnixTimeMilliseconds(),
            request.CancellationToken
        );
        return ddata.DeviceStatus.OrderByDescending(status => status.Mills);
    }

    private static double? NumberOf(Dictionary<string, object> properties, string name, string key) =>
        properties.TryGetValue(name, out var value)
        && value is Dictionary<string, object?> dictionary
        && dictionary.TryGetValue(key, out var number)
        && number != null
            ? Convert.ToDouble(number, CultureInfo.InvariantCulture)
            : null;

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
}
