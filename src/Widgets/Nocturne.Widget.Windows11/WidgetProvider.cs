using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.Widgets.Providers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Widget;
using Nocturne.Widget.Contracts;
using Nocturne.Widget.Contracts.Helpers;

namespace Nocturne.Widget.Windows11;

/// <summary>
/// Implements the Windows 11 Widget provider interface for Nocturne.
/// Uses OAuth Device Authorization Grant for secure authentication.
/// </summary>
[ComVisible(true)]
[ComDefaultInterface(typeof(IWidgetProvider))]
[Guid("B8E3F2A1-5C4D-4E6F-8A9B-1C2D3E4F5A6B")]
public sealed class NocturneWidgetProvider : IWidgetProvider, IWidgetProvider2
{
    private readonly Dictionary<string, WidgetInfo> _activeWidgets = new();
    private readonly Dictionary<string, string> _templateCache = new();
    private readonly object _widgetLock = new();

    private ICredentialStore? _credStore;
    private INocturneApiClient? _api;
    private IOAuthService? _oauth;
    private ILogger<NocturneWidgetProvider>? _log;
    private IWidgetSettingsStore? _settings;

    // Services resolve lazily on first use (a background widget update), never on the
    // COM activation thread. Building the DI container is slow on a cold start and the
    // Widgets host abandons a provider whose CreateInstance does not return promptly.
    private ICredentialStore _credentialStore => _credStore ??= Program.Services.GetRequiredService<ICredentialStore>();
    private INocturneApiClient _apiClient => _api ??= Program.Services.GetRequiredService<INocturneApiClient>();
    private IOAuthService _oauthService => _oauth ??= Program.Services.GetRequiredService<IOAuthService>();
    private ILogger<NocturneWidgetProvider> _logger => _log ??= Program.Services.GetRequiredService<ILogger<NocturneWidgetProvider>>();
    private IWidgetSettingsStore _settingsStore => _settings ??= Program.Services.GetRequiredService<IWidgetSettingsStore>();

    // Polling cancellation
    private CancellationTokenSource? _pollCts;

    // Background refresh while widgets are live. Windows tears down the provider process
    // when no widgets remain, so these are scoped to that lifetime.
    private Timer? _refreshTimer;
    private bool _realtimeWired;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private static readonly string TemplatesPath = Path.Combine(AppContext.BaseDirectory, "Templates");

    /// <summary>
    /// Widget definition IDs matching the manifest
    /// </summary>
    public static class WidgetDefinitionIds
    {
        /// <summary>Small widget showing glucose and trend only</summary>
        public const string Small = "NocturneSmall";

        /// <summary>Medium widget showing glucose, trend, IOB/COB, and urgent tracker</summary>
        public const string Medium = "NocturneMedium";

        /// <summary>Large widget showing full dashboard with multiple trackers</summary>
        public const string Large = "NocturneLarge";
    }

    /// <summary>
    /// Customization states
    /// </summary>
    private enum CustomizationState
    {
        None,
        EnterServerUrl,
        AwaitingAuthorization,
        Settings,
    }

    /// <summary>
    /// Initializes a new instance of the NocturneWidgetProvider.
    /// Required parameterless constructor for COM activation.
    /// </summary>
    public NocturneWidgetProvider()
    {
        // Keep this cheap: no service resolution here (see the lazy properties above).
    }

    private void RecoverRunningWidgets()
    {
        try
        {
            var widgetManager = WidgetManager.GetDefault();
            var existingWidgets = widgetManager.GetWidgetInfos();

            foreach (var widgetInfo in existingWidgets)
            {
                var widgetId = widgetInfo.WidgetContext.Id;
                var definitionId = widgetInfo.WidgetContext.DefinitionId;

                lock (_widgetLock)
                {
                    if (!_activeWidgets.ContainsKey(widgetId))
                    {
                        _activeWidgets[widgetId] = new WidgetInfo(widgetId, definitionId)
                        {
                            CustomState = widgetInfo.CustomState
                        };
                    }
                }
            }

            Console.WriteLine($"Recovered {existingWidgets.Length} existing widgets");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error recovering widgets: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void CreateWidget(WidgetContext widgetContext)
    {
        var widgetId = widgetContext.Id;
        var definitionId = widgetContext.DefinitionId;

        _logger.LogInformation("Creating widget {WidgetId} ({DefinitionId})", widgetId, definitionId);

        lock (_widgetLock)
        {
            _activeWidgets[widgetId] = new WidgetInfo(widgetId, definitionId);
        }

        EnsureBackgroundUpdates();
        UpdateWidget(widgetId);
    }

    /// <inheritdoc />
    public void DeleteWidget(string widgetId, string customState)
    {
        Console.WriteLine($"Deleting widget: {widgetId}");

        bool empty;
        lock (_widgetLock)
        {
            _activeWidgets.Remove(widgetId);
            empty = _activeWidgets.Count == 0;
        }

        if (empty)
        {
            _refreshTimer?.Dispose();
            _refreshTimer = null;
            if (_realtimeWired)
            {
                _ = _apiClient.DisconnectSignalRAsync();
            }
            Program.SignalEmptyWidgetList();
        }
    }

    /// <inheritdoc />
    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
    {
        var widgetId = actionInvokedArgs.WidgetContext.Id;
        var verb = actionInvokedArgs.Verb;
        var data = actionInvokedArgs.Data;

        _logger.LogInformation("Action invoked on widget {WidgetId}: {Verb}", widgetId, verb);

        switch (verb)
        {
            case "refresh":
                UpdateWidget(widgetId);
                break;

            case "openApp":
                HandleOpenAppAction(data);
                break;

            case "startAuth":
                _ = HandleStartAuthAsync(widgetId, data);
                break;

            case "openVerification":
                HandleOpenVerificationUrl();
                break;

            case "cancelAuth":
                HandleCancelAuth(widgetId);
                break;

            case "saveSettings":
                _ = HandleSaveSettingsAsync(widgetId, data);
                break;

            case "signOut":
                _ = HandleSignOutAsync(widgetId);
                break;

            case "exitCustomization":
                HandleExitCustomizationAction(widgetId);
                break;

            default:
                _logger.LogWarning("Unknown action verb: {Verb}", verb);
                break;
        }
    }

    /// <inheritdoc />
    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        var widgetId = contextChangedArgs.WidgetContext.Id;
        Console.WriteLine($"Widget context changed for {widgetId}");
        UpdateWidget(widgetId);
    }

    /// <inheritdoc />
    public void Activate(WidgetContext widgetContext)
    {
        var widgetId = widgetContext.Id;
        Console.WriteLine($"Widget activated: {widgetId}");

        lock (_widgetLock)
        {
            if (_activeWidgets.TryGetValue(widgetId, out var widgetInfo))
            {
                widgetInfo.IsActive = true;
            }
        }

        EnsureBackgroundUpdates();
        UpdateWidget(widgetId);
    }

    /// <inheritdoc />
    public void Deactivate(string widgetId)
    {
        Console.WriteLine($"Widget deactivated: {widgetId}");

        lock (_widgetLock)
        {
            if (_activeWidgets.TryGetValue(widgetId, out var widgetInfo))
            {
                widgetInfo.IsActive = false;
            }
        }
    }

    /// <inheritdoc />
    public void OnCustomizationRequested(WidgetCustomizationRequestedArgs customizationRequestedArgs)
    {
        var widgetId = customizationRequestedArgs.WidgetContext.Id;
        _logger.LogInformation("Customization requested for widget {WidgetId}", widgetId);

        // When already connected, Customize opens settings (units, sign out); otherwise it
        // starts the connect flow.
        _ = OpenCustomizationAsync(widgetId);
    }

    private async Task OpenCustomizationAsync(string widgetId)
    {
        var connected = await _credentialStore.HasCredentialsAsync();

        lock (_widgetLock)
        {
            if (_activeWidgets.TryGetValue(widgetId, out var widgetInfo))
            {
                widgetInfo.CustomizationMode = connected
                    ? CustomizationState.Settings
                    : CustomizationState.EnterServerUrl;
            }
        }

        UpdateWidget(widgetId);
    }

    private void UpdateWidget(string widgetId)
    {
        _ = UpdateWidgetAsync(widgetId);
    }

    /// <summary>
    /// Starts the periodic refresh timer and (best-effort) the SignalR real-time connection so
    /// the widget keeps current while it is live, rather than only when the user interacts.
    /// Idempotent; safe to call from every activation.
    /// </summary>
    private void EnsureBackgroundUpdates()
    {
        lock (_widgetLock)
        {
            _refreshTimer ??= new Timer(_ => RefreshLiveWidgets(), null, RefreshInterval, RefreshInterval);
        }

        _ = EnsureRealtimeAsync();
    }

    /// <summary>
    /// Pushes a fresh update to every active, connected widget (i.e. not mid-setup).
    /// </summary>
    private void RefreshLiveWidgets()
    {
        List<string> ids;
        lock (_widgetLock)
        {
            ids = _activeWidgets.Values
                .Where(w => w.IsActive && w.CustomizationMode == CustomizationState.None)
                .Select(w => w.WidgetId)
                .ToList();
        }

        foreach (var id in ids)
        {
            UpdateWidget(id);
        }
    }

    /// <summary>
    /// Subscribes to SignalR push events (new data, alarms) and connects once, if credentials
    /// exist. Failure is non-fatal — the periodic timer remains the refresh baseline.
    /// </summary>
    private async Task EnsureRealtimeAsync()
    {
        if (_realtimeWired)
        {
            return;
        }

        try
        {
            if (!await _credentialStore.HasCredentialsAsync())
            {
                return;
            }

            _realtimeWired = true;
            _apiClient.DataUpdated += (_, _) => RefreshLiveWidgets();
            _apiClient.AlarmReceived += (_, _) => RefreshLiveWidgets();
            _apiClient.AlarmCleared += (_, _) => RefreshLiveWidgets();
            await _apiClient.ConnectSignalRAsync();
            _logger.LogInformation("Realtime updates connected");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Realtime updates unavailable; using periodic refresh only");
        }
    }

    private async Task UpdateWidgetAsync(string widgetId)
    {
        try
        {
            WidgetInfo? widgetInfo;
            lock (_widgetLock)
            {
                if (!_activeWidgets.TryGetValue(widgetId, out widgetInfo))
                {
                    _logger.LogWarning("Widget not found: {WidgetId}", widgetId);
                    return;
                }
            }

            string template;
            JsonObject dataNode;

            switch (widgetInfo.CustomizationMode)
            {
                case CustomizationState.EnterServerUrl:
                    template = GetServerUrlTemplate();
                    var pendingAuth = await _credentialStore.GetDeviceAuthStateAsync();
                    var existingCreds = await _credentialStore.GetCredentialsAsync();
                    var setupSettings = await _settingsStore.GetAsync();
                    dataNode = new JsonObject
                    {
                        ["apiUrl"] = pendingAuth?.ApiUrl ?? existingCreds?.ApiUrl ?? "",
                        ["hasCredentials"] = existingCreds != null,
                        ["unit"] = setupSettings.Unit.ToString(),
                    };
                    break;

                case CustomizationState.Settings:
                    template = GetSettingsTemplate();
                    var settingsCreds = await _credentialStore.GetCredentialsAsync();
                    var savedSettings = await _settingsStore.GetAsync();
                    dataNode = new JsonObject
                    {
                        ["apiUrl"] = settingsCreds?.ApiUrl ?? "",
                        ["unit"] = savedSettings.Unit.ToString(),
                    };
                    break;

                case CustomizationState.AwaitingAuthorization:
                    var authState = await _credentialStore.GetDeviceAuthStateAsync();
                    if (authState == null)
                    {
                        widgetInfo.CustomizationMode = CustomizationState.EnterServerUrl;
                        template = GetServerUrlTemplate();
                        dataNode = new JsonObject { ["apiUrl"] = "" };
                    }
                    else
                    {
                        template = GetAuthorizationPendingTemplate();
                        dataNode = new JsonObject
                        {
                            ["userCode"] = authState.UserCode,
                            ["verificationUri"] = authState.VerificationUri,
                        };
                    }
                    break;

                default:
                    var hasCredentials = await _credentialStore.HasCredentialsAsync();

                    if (!hasCredentials)
                    {
                        template = GetSetupTemplate();
                        dataNode = new JsonObject();
                    }
                    else
                    {
                        // Small shows only the current value; Medium/Large render a trend
                        // sparkline, so they request recent history (and Large, predictions).
                        var isLarge = widgetInfo.DefinitionId == WidgetDefinitionIds.Large;
                        var isSmall = widgetInfo.DefinitionId == WidgetDefinitionIds.Small;
                        var summary = await _apiClient.GetSummaryAsync(
                            hours: isSmall ? 0 : 3,
                            includePredictions: isLarge
                        );

                        if (summary is null)
                        {
                            template = GetErrorTemplate();
                            dataNode = new JsonObject
                            {
                                ["errorMessage"] = "Unable to connect to Nocturne server"
                            };
                        }
                        else
                        {
                            var settings = await _settingsStore.GetAsync();
                            template = GetGlucoseTemplate(widgetInfo.DefinitionId);
                            dataNode = CreateGlucoseData(summary, widgetInfo.DefinitionId, settings.Unit);
                        }
                    }
                    break;
            }

            var updateOptions = new WidgetUpdateRequestOptions(widgetId)
            {
                Template = template,
                Data = dataNode.ToJsonString(),
                CustomState = widgetInfo.CustomState ?? string.Empty,
            };

            WidgetManager.GetDefault().UpdateWidget(updateOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update widget {WidgetId}", widgetId);
        }
    }

    private static string GetServerUrlTemplate()
    {
        return """
            {
                "type": "AdaptiveCard",
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "version": "1.5",
                "body": [
                    {
                        "type": "TextBlock",
                        "text": "Connect to Nocturne",
                        "size": "Medium",
                        "weight": "Bolder"
                    },
                    {
                        "type": "TextBlock",
                        "text": "Enter your Nocturne server URL to begin authentication.",
                        "size": "Small",
                        "wrap": true,
                        "isSubtle": true
                    },
                    {
                        "type": "Input.Text",
                        "id": "apiUrl",
                        "label": "Server URL",
                        "placeholder": "https://your-nocturne-server.com",
                        "value": "${apiUrl}",
                        "isRequired": true
                    },
                    {
                        "type": "Input.ChoiceSet",
                        "id": "unit",
                        "label": "Glucose units",
                        "style": "compact",
                        "value": "${unit}",
                        "choices": [
                            { "title": "mg/dL", "value": "MgDl" },
                            { "title": "mmol/L", "value": "MmolL" }
                        ]
                    }
                ],
                "actions": [
                    {
                        "type": "Action.Execute",
                        "title": "Connect",
                        "verb": "startAuth"
                    },
                    {
                        "type": "Action.Execute",
                        "title": "Cancel",
                        "verb": "exitCustomization"
                    }
                ]
            }
            """;
    }

    private static string GetAuthorizationPendingTemplate()
    {
        return """
            {
                "type": "AdaptiveCard",
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "version": "1.5",
                "body": [
                    {
                        "type": "TextBlock",
                        "text": "Authorization Required",
                        "size": "Medium",
                        "weight": "Bolder",
                        "horizontalAlignment": "Center"
                    },
                    {
                        "type": "TextBlock",
                        "text": "Visit the URL below and enter this code:",
                        "size": "Small",
                        "wrap": true,
                        "horizontalAlignment": "Center"
                    },
                    {
                        "type": "TextBlock",
                        "text": "${userCode}",
                        "size": "ExtraLarge",
                        "weight": "Bolder",
                        "horizontalAlignment": "Center",
                        "color": "Accent",
                        "spacing": "Medium"
                    },
                    {
                        "type": "TextBlock",
                        "text": "${verificationUri}",
                        "size": "Small",
                        "horizontalAlignment": "Center",
                        "spacing": "Medium"
                    },
                    {
                        "type": "TextBlock",
                        "text": "Waiting for authorization...",
                        "size": "Small",
                        "isSubtle": true,
                        "horizontalAlignment": "Center",
                        "spacing": "Large"
                    }
                ],
                "actions": [
                    {
                        "type": "Action.Execute",
                        "title": "Open in Browser",
                        "verb": "openVerification"
                    },
                    {
                        "type": "Action.Execute",
                        "title": "Cancel",
                        "verb": "cancelAuth"
                    }
                ]
            }
            """;
    }

    private static string GetSettingsTemplate()
    {
        return """
            {
                "type": "AdaptiveCard",
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "version": "1.5",
                "body": [
                    {
                        "type": "TextBlock",
                        "text": "Widget Settings",
                        "size": "Medium",
                        "weight": "Bolder"
                    },
                    {
                        "type": "TextBlock",
                        "text": "Connected to ${apiUrl}",
                        "size": "Small",
                        "wrap": true,
                        "isSubtle": true,
                        "spacing": "None"
                    },
                    {
                        "type": "Input.ChoiceSet",
                        "id": "unit",
                        "label": "Glucose units",
                        "style": "compact",
                        "value": "${unit}",
                        "choices": [
                            { "title": "mg/dL", "value": "MgDl" },
                            { "title": "mmol/L", "value": "MmolL" }
                        ]
                    }
                ],
                "actions": [
                    {
                        "type": "Action.Execute",
                        "title": "Save",
                        "verb": "saveSettings",
                        "style": "positive"
                    },
                    {
                        "type": "Action.Execute",
                        "title": "Sign out",
                        "verb": "signOut",
                        "style": "destructive"
                    },
                    {
                        "type": "Action.Execute",
                        "title": "Done",
                        "verb": "exitCustomization"
                    }
                ]
            }
            """;
    }

    private static string GetSetupTemplate()
    {
        return """
            {
                "type": "AdaptiveCard",
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "version": "1.5",
                "body": [
                    {
                        "type": "Container",
                        "items": [
                            {
                                "type": "TextBlock",
                                "text": "Setup Required",
                                "size": "Large",
                                "weight": "Bolder",
                                "horizontalAlignment": "Center"
                            },
                            {
                                "type": "TextBlock",
                                "text": "Click the ... menu and select Customize to connect to your Nocturne server",
                                "size": "Small",
                                "horizontalAlignment": "Center",
                                "wrap": true,
                                "isSubtle": true,
                                "spacing": "Medium"
                            }
                        ],
                        "verticalContentAlignment": "Center",
                        "height": "stretch"
                    }
                ]
            }
            """;
    }

    private static string GetErrorTemplate()
    {
        return """
            {
                "type": "AdaptiveCard",
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "version": "1.5",
                "body": [
                    {
                        "type": "Container",
                        "items": [
                            {
                                "type": "TextBlock",
                                "text": "Connection Error",
                                "size": "Medium",
                                "weight": "Bolder",
                                "horizontalAlignment": "Center",
                                "color": "Attention"
                            },
                            {
                                "type": "TextBlock",
                                "text": "${errorMessage}",
                                "size": "Small",
                                "horizontalAlignment": "Center",
                                "wrap": true,
                                "isSubtle": true,
                                "spacing": "Small"
                            }
                        ],
                        "verticalContentAlignment": "Center",
                        "height": "stretch"
                    }
                ],
                "actions": [
                    {
                        "type": "Action.Execute",
                        "title": "Retry",
                        "verb": "refresh"
                    }
                ]
            }
            """;
    }

    private string GetGlucoseTemplate(string definitionId)
    {
        if (_templateCache.TryGetValue(definitionId, out var cached))
            return cached;

        var templateFileName = definitionId switch
        {
            WidgetDefinitionIds.Small => "SmallTemplate.json",
            WidgetDefinitionIds.Medium => "MediumTemplate.json",
            WidgetDefinitionIds.Large => "LargeTemplate.json",
            _ => "SmallTemplate.json",
        };

        var templatePath = Path.Combine(TemplatesPath, templateFileName);

        try
        {
            var template = File.ReadAllText(templatePath);
            _templateCache[definitionId] = template;
            return template;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load template {Path}, using fallback", templatePath);
            return GetFallbackGlucoseTemplate();
        }
    }

    private static string GetFallbackGlucoseTemplate()
    {
        return """
            {
                "type": "AdaptiveCard",
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "version": "1.5",
                "body": [
                    {
                        "type": "TextBlock",
                        "text": "${glucose} ${direction}",
                        "size": "ExtraLarge",
                        "weight": "Bolder",
                        "horizontalAlignment": "Center"
                    },
                    {
                        "type": "TextBlock",
                        "text": "${delta}",
                        "size": "Small",
                        "horizontalAlignment": "Center",
                        "isSubtle": true
                    }
                ],
                "actions": [{ "type": "Action.Execute", "title": "Refresh", "verb": "refresh" }],
                "selectAction": { "type": "Action.Execute", "verb": "openApp" }
            }
            """;
    }

    private static JsonObject CreateGlucoseData(
        V4SummaryResponse summary,
        string definitionId,
        GlucoseUnit unit
    )
    {
        var current = summary.Current;
        var glucose = current is not null
            ? GlucoseFormatHelper.FormatValue(current.Sgv, unit)
            : "---";
        var direction = DirectionHelper.GetArrowText(current?.Direction.ToString());
        var delta = GlucoseFormatHelper.FormatDelta(current?.Delta, unit);

        // Calculate staleness and relative time
        var stale = false;
        var lastUpdate = "";
        if (current is not null)
        {
            var ageMs = summary.ServerMills - current.Mills;
            stale = TimeAgoHelper.IsStaleMilliseconds(ageMs);
            lastUpdate = TimeAgoHelper.FormatMilliseconds(ageMs);
        }

        var rate = current?.TrendRate;
        var data = new JsonObject
        {
            ["glucose"] = glucose,
            ["direction"] = direction,
            ["delta"] = delta,
            ["units"] = unit == GlucoseUnit.MmolL ? "mmol/L" : "mg/dL",
            ["trendRate"] = rate is not null ? $"{GlucoseFormatHelper.FormatDelta(rate, unit)}/min" : "",
            ["lastUpdate"] = lastUpdate,
            ["stale"] = stale,
        };

        // Active-alarm banner (suppressed while the alarm is silenced)
        var alarm = summary.Alarm;
        var hasAlarm = alarm is not null && !alarm.IsSilenced;
        data["hasAlarm"] = hasAlarm;
        data["alarmMessage"] = hasAlarm ? alarm!.Message : "";
        data["alarmColor"] = IsUrgentAlarm(alarm) ? "Attention" : "Warning";

        // IOB, COB, and trend sparkline for Medium and Large
        if (definitionId is WidgetDefinitionIds.Medium or WidgetDefinitionIds.Large)
        {
            data["iob"] = Math.Round(summary.Iob * 100) / 100;
            data["cob"] = (int)summary.Cob;

            var (chartW, chartH) = definitionId == WidgetDefinitionIds.Large ? (600, 200) : (600, 150);
            data["chart"] = GlucoseSparkline.RenderDataUri(summary, chartW, chartH) ?? "";
        }

        // Predictions and trackers for Large
        if (definitionId == WidgetDefinitionIds.Large)
        {
            data["predictions"] = BuildPredictionsArray(summary.Predictions, unit);
            data["trackers"] = BuildTrackersArray(summary.Trackers);
        }

        return data;
    }

    private static JsonArray BuildPredictionsArray(V4Predictions? predictions, GlucoseUnit unit)
    {
        var result = new JsonArray();
        if (predictions?.Values is null || predictions.Values.Count == 0)
            return result;

        // Sample at +15, +30, +45, +60 minutes
        var intervalMs = predictions.IntervalMills;
        if (intervalMs <= 0) return result;

        foreach (var targetMin in new[] { 15, 30, 45, 60 })
        {
            var index = (int)(targetMin * 60_000L / intervalMs);
            if (index >= 0 && index < predictions.Values.Count)
            {
                result.Add(new JsonObject
                {
                    ["value"] = GlucoseFormatHelper.FormatValue(predictions.Values[index], unit),
                    ["time"] = $"+{targetMin}m",
                });
            }
        }

        return result;
    }

    private static bool IsUrgentAlarm(V4AlarmState? alarm) =>
        alarm is not null
        && (alarm.Level >= 2 || alarm.Type.Contains("urgent", StringComparison.OrdinalIgnoreCase));

    private static JsonArray BuildTrackersArray(List<V4TrackerStatus> trackers)
    {
        var result = new JsonArray();
        foreach (var tracker in trackers)
        {
            var urgencyColor = tracker.Urgency switch
            {
                NotificationUrgency.Urgent => "Attention",
                NotificationUrgency.Hazard
                    or NotificationUrgency.Warn => "Warning",
                _ => "Default",
            };

            var age = tracker.AgeHours.HasValue
                ? FormatTrackerAge(tracker.AgeHours.Value)
                : tracker.HoursUntilEvent.HasValue
                    ? $"in {FormatTrackerAge(Math.Abs(tracker.HoursUntilEvent.Value))}"
                    : "";

            // Lifespan progress (Duration trackers only); empty for event-mode trackers.
            var percentLabel = tracker.PercentElapsed is { } pe
                ? $"{Math.Clamp((int)Math.Round(pe), 0, 100)}% used"
                : "";

            result.Add(new JsonObject
            {
                ["name"] = tracker.Name ?? "",
                ["age"] = age,
                ["urgencyColor"] = urgencyColor,
                ["percentLabel"] = percentLabel,
            });
        }
        return result;
    }

    private static string FormatTrackerAge(double hours)
    {
        if (hours < 1) return $"{(int)(hours * 60)}m";
        if (hours < 48) return $"{hours:F0}h";
        return $"{hours / 24:F1}d";
    }

    private void HandleOpenAppAction(string data)
    {
        try
        {
            var uri = string.IsNullOrEmpty(data) ? "nocturne://" : $"nocturne://{data}";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch Nocturne app");
        }
    }

    private async Task HandleStartAuthAsync(string widgetId, string data)
    {
        try
        {
            var formData = JsonSerializer.Deserialize<JsonElement>(data);
            var apiUrl = formData.TryGetProperty("apiUrl", out var urlProp) ? urlProp.GetString() : null;

            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                _logger.LogWarning("Missing API URL for authentication");
                return;
            }

            // Persist the units chosen on the connect card before authorizing.
            var settings = await _settingsStore.GetAsync();
            settings.Unit = ParseUnit(data, settings.Unit);
            await _settingsStore.SaveAsync(settings);

            _logger.LogInformation("Starting OAuth device flow for {ApiUrl}", apiUrl);

            var result = await _oauthService.InitiateDeviceAuthorizationAsync(apiUrl);

            if (!result.Success)
            {
                _logger.LogWarning("Failed to initiate device authorization: {Error}", result.Error);
                // TODO: Show error in widget
                return;
            }

            lock (_widgetLock)
            {
                if (_activeWidgets.TryGetValue(widgetId, out var widgetInfo))
                {
                    widgetInfo.CustomizationMode = CustomizationState.AwaitingAuthorization;
                }
            }

            UpdateWidget(widgetId);

            // Start polling for authorization
            _ = PollForAuthorizationAsync(widgetId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting authentication");
        }
    }

    private async Task PollForAuthorizationAsync(string widgetId)
    {
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;

        try
        {
            var authState = await _credentialStore.GetDeviceAuthStateAsync();
            if (authState == null)
            {
                return;
            }

            var interval = Math.Max(authState.Interval, 5);

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), ct);

                if (ct.IsCancellationRequested) break;

                var result = await _oauthService.PollForAuthorizationAsync();

                if (result.Success)
                {
                    _logger.LogInformation("OAuth authorization completed successfully");

                    lock (_widgetLock)
                    {
                        if (_activeWidgets.TryGetValue(widgetId, out var widgetInfo))
                        {
                            widgetInfo.CustomizationMode = CustomizationState.None;
                        }
                    }

                    UpdateWidget(widgetId);
                    return;
                }

                if (result.SlowDown)
                {
                    interval = Math.Min(interval + 5, 30);
                    _logger.LogDebug("Slowing down polling to {Interval}s", interval);
                }

                if (result.Expired || result.AccessDenied)
                {
                    _logger.LogWarning("Authorization failed: Expired={Expired}, Denied={Denied}",
                        result.Expired, result.AccessDenied);

                    await _credentialStore.ClearDeviceAuthStateAsync();

                    lock (_widgetLock)
                    {
                        if (_activeWidgets.TryGetValue(widgetId, out var widgetInfo))
                        {
                            widgetInfo.CustomizationMode = CustomizationState.EnterServerUrl;
                        }
                    }

                    UpdateWidget(widgetId);
                    return;
                }

                if (!result.Pending)
                {
                    _logger.LogWarning("Unexpected poll result: {Error}", result.Error);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Authorization polling cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during authorization polling");
        }
    }

    private void HandleOpenVerificationUrl()
    {
        _ = OpenVerificationUrlAsync();
    }

    private async Task OpenVerificationUrlAsync()
    {
        try
        {
            var authState = await _credentialStore.GetDeviceAuthStateAsync();
            if (authState == null) return;

            var url = authState.VerificationUriComplete ?? authState.VerificationUri;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open verification URL");
        }
    }

    private void HandleCancelAuth(string widgetId)
    {
        _pollCts?.Cancel();
        _ = _credentialStore.ClearDeviceAuthStateAsync();

        lock (_widgetLock)
        {
            if (_activeWidgets.TryGetValue(widgetId, out var widgetInfo))
            {
                widgetInfo.CustomizationMode = CustomizationState.EnterServerUrl;
            }
        }

        UpdateWidget(widgetId);
    }

    private async Task HandleSaveSettingsAsync(string widgetId, string data)
    {
        try
        {
            var settings = await _settingsStore.GetAsync();
            settings.Unit = ParseUnit(data, settings.Unit);
            await _settingsStore.SaveAsync(settings);
            _logger.LogInformation("Widget settings saved (unit={Unit})", settings.Unit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving widget settings");
        }

        lock (_widgetLock)
        {
            if (_activeWidgets.TryGetValue(widgetId, out var widgetInfo))
            {
                widgetInfo.CustomizationMode = CustomizationState.None;
            }
        }

        UpdateWidget(widgetId);
    }

    /// <summary>
    /// Reads the "unit" input value from an Action.Execute payload, falling back to the
    /// current value if absent or unrecognised.
    /// </summary>
    private static GlucoseUnit ParseUnit(string data, GlucoseUnit fallback)
    {
        if (string.IsNullOrWhiteSpace(data))
            return fallback;

        try
        {
            var form = JsonSerializer.Deserialize<JsonElement>(data);
            if (form.TryGetProperty("unit", out var unitProp)
                && Enum.TryParse<GlucoseUnit>(unitProp.GetString(), out var parsed))
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
            // Malformed payload; keep the existing setting.
        }

        return fallback;
    }

    private async Task HandleSignOutAsync(string widgetId)
    {
        try
        {
            await _oauthService.SignOutAsync();
            _logger.LogInformation("Signed out successfully");

            lock (_widgetLock)
            {
                if (_activeWidgets.TryGetValue(widgetId, out var widgetInfo))
                {
                    widgetInfo.CustomizationMode = CustomizationState.None;
                }
            }

            UpdateWidget(widgetId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during sign out");
        }
    }

    private void HandleExitCustomizationAction(string widgetId)
    {
        _logger.LogInformation("Exiting customization for widget {WidgetId}", widgetId);

        _pollCts?.Cancel();

        lock (_widgetLock)
        {
            if (_activeWidgets.TryGetValue(widgetId, out var widgetInfo))
            {
                widgetInfo.CustomizationMode = CustomizationState.None;
            }
        }

        UpdateWidget(widgetId);
    }

    /// <summary>
    /// Information about an active widget instance
    /// </summary>
    private sealed class WidgetInfo
    {
        public string WidgetId { get; }
        public string DefinitionId { get; }
        public bool IsActive { get; set; }
        public string? CustomState { get; set; }
        public CustomizationState CustomizationMode { get; set; }

        public WidgetInfo(string widgetId, string definitionId)
        {
            WidgetId = widgetId;
            DefinitionId = definitionId;
            IsActive = true;
            CustomizationMode = CustomizationState.None;
        }
    }
}
