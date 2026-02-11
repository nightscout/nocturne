using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.Widgets.Providers;
using Nocturne.Widget.Contracts;

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

    private readonly ICredentialStore _credentialStore;
    private readonly INocturneApiClient _apiClient;
    private readonly IOAuthService _oauthService;
    private readonly ILogger<NocturneWidgetProvider> _logger;

    // Polling cancellation
    private CancellationTokenSource? _pollCts;

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
    }

    /// <summary>
    /// Initializes a new instance of the NocturneWidgetProvider.
    /// Required parameterless constructor for COM activation.
    /// </summary>
    public NocturneWidgetProvider()
    {
        Console.WriteLine("NocturneWidgetProvider initialized");

        // Resolve services from the static service provider
        _credentialStore = Program.Services.GetRequiredService<ICredentialStore>();
        _apiClient = Program.Services.GetRequiredService<INocturneApiClient>();
        _oauthService = Program.Services.GetRequiredService<IOAuthService>();
        _logger = Program.Services.GetRequiredService<ILogger<NocturneWidgetProvider>>();
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

        Console.WriteLine($"Creating widget: {widgetId} with definition: {definitionId}");

        lock (_widgetLock)
        {
            _activeWidgets[widgetId] = new WidgetInfo(widgetId, definitionId);
        }

        UpdateWidget(widgetId);
    }

    /// <inheritdoc />
    public void DeleteWidget(string widgetId, string customState)
    {
        Console.WriteLine($"Deleting widget: {widgetId}");

        lock (_widgetLock)
        {
            _activeWidgets.Remove(widgetId);

            if (_activeWidgets.Count == 0)
            {
                Program.SignalEmptyWidgetList();
            }
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

        lock (_widgetLock)
        {
            if (_activeWidgets.TryGetValue(widgetId, out var widgetInfo))
            {
                widgetInfo.CustomizationMode = CustomizationState.EnterServerUrl;
                UpdateWidget(widgetId);
            }
        }
    }

    private void UpdateWidget(string widgetId)
    {
        _ = UpdateWidgetAsync(widgetId);
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
                    dataNode = new JsonObject
                    {
                        ["apiUrl"] = pendingAuth?.ApiUrl ?? existingCreds?.ApiUrl ?? "",
                        ["hasCredentials"] = existingCreds != null,
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
                        var needsPredictions = widgetInfo.DefinitionId == WidgetDefinitionIds.Large;
                        var summary = await _apiClient.GetSummaryAsync(
                            hours: 0,
                            includePredictions: needsPredictions
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
                            template = GetGlucoseTemplate(widgetInfo.DefinitionId);
                            dataNode = CreateGlucoseData(summary, widgetInfo.DefinitionId);
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
                                "text": "Nocturne",
                                "size": "Large",
                                "weight": "Bolder",
                                "horizontalAlignment": "Center"
                            },
                            {
                                "type": "TextBlock",
                                "text": "Setup Required",
                                "size": "Medium",
                                "horizontalAlignment": "Center",
                                "spacing": "Small"
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

    private static JsonObject CreateGlucoseData(V4SummaryResponse summary, string definitionId)
    {
        var current = summary.Current;
        var glucose = current is not null ? ((int)current.Sgv).ToString() : "---";
        var direction = GetDirectionArrow(current?.Direction);
        var delta = FormatDelta(current?.Delta);

        // Calculate staleness and relative time
        var stale = false;
        var lastUpdate = "";
        if (current is not null)
        {
            var ageMs = summary.ServerMills - current.Mills;
            stale = ageMs > 15 * 60 * 1000; // 15 minutes
            lastUpdate = FormatRelativeTime(ageMs);
        }

        var data = new JsonObject
        {
            ["glucose"] = glucose,
            ["direction"] = direction,
            ["delta"] = delta,
            ["lastUpdate"] = lastUpdate,
            ["stale"] = stale,
        };

        // IOB and COB for Medium and Large
        if (definitionId is WidgetDefinitionIds.Medium or WidgetDefinitionIds.Large)
        {
            data["iob"] = Math.Round(summary.Iob * 100) / 100;
            data["cob"] = (int)summary.Cob;
        }

        // Predictions and trackers for Large
        if (definitionId == WidgetDefinitionIds.Large)
        {
            data["predictions"] = BuildPredictionsArray(summary.Predictions);
            data["trackers"] = BuildTrackersArray(summary.Trackers);
        }

        return data;
    }

    private static string FormatDelta(double? delta)
    {
        if (delta is null) return "";
        var sign = delta >= 0 ? "+" : "";
        return $"{sign}{delta:F1}";
    }

    private static string FormatRelativeTime(long milliseconds)
    {
        if (milliseconds < 0) return "now";
        var minutes = milliseconds / 60_000;
        if (minutes < 1) return "now";
        if (minutes < 60) return $"{minutes}m ago";
        var hours = minutes / 60;
        if (hours < 24) return $"{hours}h ago";
        var days = hours / 24;
        return $"{days}d ago";
    }

    private static JsonArray BuildPredictionsArray(V4Predictions? predictions)
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
                    ["value"] = ((int)predictions.Values[index]).ToString(),
                    ["time"] = $"+{targetMin}m",
                });
            }
        }

        return result;
    }

    private static JsonArray BuildTrackersArray(List<V4TrackerStatus> trackers)
    {
        var result = new JsonArray();
        foreach (var tracker in trackers)
        {
            var urgencyColor = tracker.Urgency?.ToUpperInvariant() switch
            {
                "URGENT" => "Attention",
                "HAZARD" or "WARN" => "Warning",
                _ => "Default",
            };

            var age = tracker.AgeHours.HasValue
                ? FormatTrackerAge(tracker.AgeHours.Value)
                : tracker.HoursUntilEvent.HasValue
                    ? $"in {FormatTrackerAge(Math.Abs(tracker.HoursUntilEvent.Value))}"
                    : "";

            result.Add(new JsonObject
            {
                ["name"] = tracker.Name ?? "",
                ["age"] = age,
                ["urgencyColor"] = urgencyColor,
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

    private static string GetDirectionArrow(string? direction)
    {
        return direction?.ToUpperInvariant() switch
        {
            "DOUBLEUP" or "DOUBLE_UP" => "\u21C8",
            "SINGLEUP" or "SINGLE_UP" or "UP" => "\u2191",
            "FORTYFIVEUP" or "FORTY_FIVE_UP" => "\u2197",
            "FLAT" => "\u2192",
            "FORTYFIVEDOWN" or "FORTY_FIVE_DOWN" => "\u2198",
            "SINGLEDOWN" or "SINGLE_DOWN" or "DOWN" => "\u2193",
            "DOUBLEDOWN" or "DOUBLE_DOWN" => "\u21CA",
            "NOT_COMPUTABLE" or "NONE" or null => "?",
            _ => direction ?? "?"
        };
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
