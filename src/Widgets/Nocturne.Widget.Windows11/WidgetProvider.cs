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
    private IGlucoseFilePublisher? _publisher;

    // Services resolve lazily on first use (a background widget update), never on the
    // COM activation thread. Building the DI container is slow on a cold start and the
    // Widgets host abandons a provider whose CreateInstance does not return promptly.
    private ICredentialStore _credentialStore => _credStore ??= Program.Services.GetRequiredService<ICredentialStore>();
    private INocturneApiClient _apiClient => _api ??= Program.Services.GetRequiredService<INocturneApiClient>();
    private IOAuthService _oauthService => _oauth ??= Program.Services.GetRequiredService<IOAuthService>();
    private ILogger<NocturneWidgetProvider> _logger => _log ??= Program.Services.GetRequiredService<ILogger<NocturneWidgetProvider>>();
    private IWidgetSettingsStore _settingsStore => _settings ??= Program.Services.GetRequiredService<IWidgetSettingsStore>();
    private IGlucoseFilePublisher _glucoseFilePublisher => _publisher ??= Program.Services.GetRequiredService<IGlucoseFilePublisher>();

    // Polling cancellation
    private CancellationTokenSource? _pollCts;

    // Background refresh while widgets are live. Windows tears down the provider process
    // when no widgets remain, so these are scoped to that lifetime.
    private Timer? _refreshTimer;
    private bool _realtimeWired;
    private int _publishing;  // 0/1 guard so overlapping ticks don't double-publish the glucose file
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private static readonly string TemplatesPath = Path.Combine(AppContext.BaseDirectory, "Templates");

    /// <summary>
    /// Widget definition IDs matching the manifest
    /// </summary>
    public static class WidgetDefinitionIds
    {
        /// <summary>Glucose widget rendered at small, medium, or large size.</summary>
        public const string Glucose = "NocturneGlucose";

        /// <summary>Daily glucose stats: average, GMI, variability</summary>
        public const string Stats = "NocturneStats";

        /// <summary>Time-in-range bar for the last 24 hours</summary>
        public const string Tir = "NocturneTir";

        /// <summary>Time-in-range percentages stacked vertically (compact)</summary>
        public const string TirStacked = "NocturneTirStacked";

        /// <summary>Device ages: cannula, sensor, insulin, battery</summary>
        public const string Ages = "NocturneAges";

        /// <summary>Loop/AID status from the latest algorithm snapshot</summary>
        public const string Loop = "NocturneLoop";
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
                            CustomState = widgetInfo.CustomState,
                            Size = SizeOf(widgetInfo.WidgetContext),
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
            _activeWidgets[widgetId] = new WidgetInfo(widgetId, definitionId)
            {
                Size = SizeOf(widgetContext),
            };
        }

        EnsureBackgroundUpdates();
        UpdateWidget(widgetId);
    }

    /// <summary>The widget's current size as a lowercase string ("small"/"medium"/"large").</summary>
    private static string SizeOf(WidgetContext widgetContext) =>
        widgetContext.Size.ToString().ToLowerInvariant();

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

        // The user resized the widget — record the new size so the glucose widget re-renders
        // the size-appropriate template.
        lock (_widgetLock)
        {
            if (_activeWidgets.TryGetValue(widgetId, out var widgetInfo))
            {
                widgetInfo.Size = SizeOf(contextChangedArgs.WidgetContext);
            }
        }

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
                widgetInfo.Size = SizeOf(widgetContext);
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
        bool firstSetup = false;
        lock (_widgetLock)
        {
            if (_refreshTimer is null)
            {
                _refreshTimer = new Timer(_ => OnRefreshTick(), null, RefreshInterval, RefreshInterval);
                firstSetup = true;
            }
        }

        // Publish the glucose file immediately on first setup so the taskbar mod has fresh data
        // without waiting a full refresh interval.
        if (firstSetup)
        {
            _ = PublishGlucoseFileAsync();
        }

        _ = EnsureRealtimeAsync();
    }

    /// <summary>
    /// Periodic/real-time tick: refresh the live widget UIs and republish the shared glucose file.
    /// </summary>
    private void OnRefreshTick()
    {
        RefreshLiveWidgets();
        _ = PublishGlucoseFileAsync();
    }

    /// <summary>
    /// Fetches the raw V4 summary and writes it to the local glucose file the taskbar mod reads,
    /// mirroring the desktop companion so either can supply data. Best-effort: requires credentials,
    /// fetches the same window the companion does (so the file is equally rich regardless of which
    /// widgets are pinned), and never throws into the caller.
    /// </summary>
    private async Task PublishGlucoseFileAsync()
    {
        // Skip if a publish is already in flight: the timer and SignalR pushes can fire together, and
        // there is no value in two concurrent summary fetches racing to write the same file.
        if (Interlocked.Exchange(ref _publishing, 1) == 1)
        {
            return;
        }

        try
        {
            if (!await _credentialStore.HasCredentialsAsync())
            {
                return;
            }

            var raw = await _apiClient.GetSummaryRawAsync(hours: 3, includePredictions: true);
            if (!string.IsNullOrEmpty(raw))
            {
                await _glucoseFilePublisher.PublishAsync(raw);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Glucose file publish skipped");
        }
        finally
        {
            Interlocked.Exchange(ref _publishing, 0);
        }
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
            _apiClient.DataUpdated += (_, _) => OnRefreshTick();
            _apiClient.AlarmReceived += (_, _) => OnRefreshTick();
            _apiClient.AlarmCleared += (_, _) => OnRefreshTick();
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
                        (template, dataNode) = await BuildAuthenticatedContentAsync(widgetInfo);
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
        // Kept compact so setup fits the small widget footprint (~146px) without
        // clipping the input or pushing the actions off-screen: the label,
        // description, and unit picker are dropped. Units default from settings
        // and remain editable in the Settings card after connecting.
        return """
            {
                "type": "AdaptiveCard",
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "version": "1.5",
                "body": [
                    {
                        "type": "TextBlock",
                        "text": "Connect to Nocturne",
                        "weight": "Bolder",
                        "wrap": true,
                        "spacing": "None"
                    },
                    {
                        "type": "Input.Text",
                        "id": "apiUrl",
                        "placeholder": "https://your-server.com",
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
        // Compact so the code and link stay visible in the small widget: the
        // verbose header and "Waiting..." footer are dropped, the prompt folded
        // into one line, and the code stepped down from ExtraLarge to Large.
        return """
            {
                "type": "AdaptiveCard",
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "version": "1.5",
                "body": [
                    {
                        "type": "TextBlock",
                        "text": "Enter this code to authorize:",
                        "size": "Small",
                        "wrap": true,
                        "horizontalAlignment": "Center",
                        "spacing": "None"
                    },
                    {
                        "type": "TextBlock",
                        "text": "${userCode}",
                        "size": "Large",
                        "weight": "Bolder",
                        "horizontalAlignment": "Center",
                        "color": "Accent",
                        "spacing": "Small"
                    },
                    {
                        "type": "TextBlock",
                        "text": "${verificationUri}",
                        "size": "Small",
                        "isSubtle": true,
                        "wrap": true,
                        "horizontalAlignment": "Center",
                        "spacing": "Small"
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
                        "text": "Settings",
                        "weight": "Bolder",
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
                                "size": "Medium",
                                "weight": "Bolder",
                                "horizontalAlignment": "Center",
                                "spacing": "None"
                            },
                            {
                                "type": "TextBlock",
                                "text": "Open the ... menu and select Customize to connect.",
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

    private string GetGlucoseTemplate(string size) => GetTemplate(size switch
    {
        "large" => "LargeTemplate.json",
        "medium" => "MediumTemplate.json",
        _ => "SmallTemplate.json",
    });

    /// <summary>Loads and caches an Adaptive Card template from the Templates folder by file name.</summary>
    private string GetTemplate(string fileName)
    {
        if (_templateCache.TryGetValue(fileName, out var cached))
            return cached;

        var templatePath = Path.Combine(TemplatesPath, fileName);

        try
        {
            var template = File.ReadAllText(templatePath);
            _templateCache[fileName] = template;
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
        string size,
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
            ["refreshIcon"] = RefreshIcon.DataUri,
        };

        // Active-alarm banner (suppressed while the alarm is silenced)
        var alarm = summary.Alarm;
        var hasAlarm = alarm is not null && !alarm.IsSilenced;
        data["hasAlarm"] = hasAlarm;
        data["alarmMessage"] = hasAlarm ? alarm!.Message : "";
        data["alarmColor"] = IsUrgentAlarm(alarm) ? "Attention" : "Warning";

        // IOB, COB, and trend sparkline for Medium and Large
        if (size is "medium" or "large")
        {
            data["iob"] = Math.Round(summary.Iob * 100) / 100;
            data["cob"] = (int)summary.Cob;

            var (chartW, chartH) = size == "large" ? (600, 200) : (600, 150);
            data["chart"] = GlucoseSparkline.RenderDataUri(summary, chartW, chartH) ?? "";
        }

        return data;
    }

    private static bool IsUrgentAlarm(V4AlarmState? alarm) =>
        alarm is not null
        && (alarm.Level >= 2 || alarm.Type.Contains("urgent", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Builds the Adaptive Card template and bound data for an authenticated widget, dispatching by
    /// widget type. Each focused widget pulls only the data it shows from its dedicated endpoint.
    /// </summary>
    private async Task<(string Template, JsonObject Data)> BuildAuthenticatedContentAsync(WidgetInfo widgetInfo)
    {
        return widgetInfo.DefinitionId switch
        {
            WidgetDefinitionIds.Stats => await BuildStatsContentAsync(),
            WidgetDefinitionIds.Tir => await BuildTirContentAsync("TirTemplate.json"),
            WidgetDefinitionIds.TirStacked => await BuildTirContentAsync("TirStackedTemplate.json"),
            WidgetDefinitionIds.Ages => await BuildAgesContentAsync(),
            WidgetDefinitionIds.Loop => await BuildLoopContentAsync(),
            // The glucose widget is one definition rendered at three sizes (also the safe default).
            WidgetDefinitionIds.Glucose => await BuildGlucoseContentAsync(widgetInfo.Size),
            _ => await BuildGlucoseContentAsync(widgetInfo.Size),
        };
    }

    /// <summary>Connection-error card shown when an endpoint can't be reached.</summary>
    private (string, JsonObject) ConnectionError() =>
        (GetErrorTemplate(), new JsonObject { ["errorMessage"] = "Unable to connect to Nocturne server" });

    private async Task<(string, JsonObject)> BuildGlucoseContentAsync(string size)
    {
        // Small shows only the current value; Medium/Large render a trend sparkline, so they
        // request recent history (and Large, predictions to draw the dashed forecast tail).
        var isLarge = size == "large";
        var isSmall = size == "small";
        var summary = await _apiClient.GetSummaryAsync(hours: isSmall ? 0 : 3, includePredictions: isLarge);
        if (summary is null)
        {
            return ConnectionError();
        }

        var settings = await _settingsStore.GetAsync();
        return (GetGlucoseTemplate(size), CreateGlucoseData(summary, size, settings.Unit));
    }

    private async Task<(string, JsonObject)> BuildStatsContentAsync()
    {
        var stats = await _apiClient.GetStatisticsAsync();
        if (stats is null)
        {
            return ConnectionError();
        }

        var settings = await _settingsStore.GetAsync();
        var day = stats.LastDay;
        var analytics = day?.Analytics;
        var has = day is { HasSufficientData: true } && analytics is not null;

        var data = new JsonObject { ["hasStats"] = has, ["refreshIcon"] = RefreshIcon.DataUri };
        if (has)
        {
            data["statAvg"] = GlucoseFormatHelper.FormatValue(analytics!.BasicStats.Mean, settings.Unit);
            data["statGmi"] = day!.Gmi is { } gmi ? $"{gmi.Value:0.0}%" : "--";
            data["statCv"] = $"{analytics.GlycemicVariability.CoefficientOfVariation:0}%";
        }

        return (GetTemplate("StatsTemplate.json"), data);
    }

    private async Task<(string, JsonObject)> BuildTirContentAsync(string templateFile)
    {
        var stats = await _apiClient.GetStatisticsAsync();
        if (stats is null)
        {
            return ConnectionError();
        }

        var day = stats.LastDay;
        var pct = day?.Analytics?.TimeInRange?.Percentages;
        var has = day is { HasSufficientData: true } && pct is not null;

        var data = new JsonObject { ["hasTir"] = has, ["refreshIcon"] = RefreshIcon.DataUri };
        if (has)
        {
            var low = (int)Math.Round(pct!.VeryLow + pct.Low);
            var inRange = (int)Math.Round(pct.Target);
            var high = (int)Math.Round(pct.High + pct.VeryHigh);
            data["tirLow"] = $"{low}%";
            data["tirInRange"] = $"{inRange}%";
            data["tirHigh"] = $"{high}%";
            data["tirBar"] = TirBar.RenderDataUri(low, inRange, high, 600, 36);
            data["tirBarVertical"] = TirBar.RenderDataUri(low, inRange, high, 40, 240, vertical: true);
        }

        return (GetTemplate(templateFile), data);
    }

    private async Task<(string, JsonObject)> BuildAgesContentAsync()
    {
        var ages = await _apiClient.GetDeviceAgesAsync();
        if (ages is null)
        {
            return ConnectionError();
        }

        var rows = new JsonArray();
        AddAgeRow(rows, "Cannula", ages.Cage);
        AddAgeRow(rows, "Sensor", ages.Sage?.SensorStart.Found == true ? ages.Sage.SensorStart : ages.Sage?.SensorChange);
        AddAgeRow(rows, "Insulin", ages.Iage);
        AddAgeRow(rows, "Battery", ages.Bage);

        var data = new JsonObject { ["ages"] = rows, ["refreshIcon"] = RefreshIcon.DataUri };
        return (GetTemplate("AgesTemplate.json"), data);
    }

    private static void AddAgeRow(JsonArray rows, string label, DeviceAgeInfo? info)
    {
        if (info is not { Found: true })
        {
            return;
        }

        // Device-age levels: 2 = urgent, 1 = warn, else within lifespan.
        var color = info.Level switch
        {
            >= 2 => "Attention",
            1 => "Warning",
            _ => "Default",
        };

        rows.Add(new JsonObject
        {
            ["label"] = label,
            ["value"] = info.Display,
            ["color"] = color,
        });
    }

    private async Task<(string, JsonObject)> BuildLoopContentAsync()
    {
        var page = await _apiClient.GetLoopStatusAsync();
        if (page is null)
        {
            return ConnectionError();
        }

        var snapshot = page.Data?.FirstOrDefault();
        var settings = await _settingsStore.GetAsync();
        var data = new JsonObject { ["hasLoop"] = snapshot is not null, ["refreshIcon"] = RefreshIcon.DataUri };

        if (snapshot is not null)
        {
            var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                - new DateTimeOffset(snapshot.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();
            var loopStale = ageMs > 15 * 60 * 1000L;

            data["algorithm"] = snapshot.AidAlgorithm.ToString();
            data["loopStatus"] = snapshot.Enacted ? "Looping" : "Open loop";
            data["loopColor"] = loopStale ? "Attention" : (snapshot.Enacted ? "Good" : "Warning");
            data["lastRun"] = TimeAgoHelper.FormatMilliseconds(ageMs);
            data["tempBasal"] = snapshot.Enacted && snapshot.EnactedRate is { } r ? $"{r:0.##} U/hr" : "";
            data["iob"] = snapshot.Iob is { } iob ? $"{iob:0.##} U" : "--";
            data["eventualBg"] = snapshot.EventualBg is { } ev
                ? GlucoseFormatHelper.FormatValue(ev, settings.Unit)
                : "--";
        }

        return (GetTemplate("LoopTemplate.json"), data);
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

        /// <summary>Current pinned size ("small"/"medium"/"large"); the glucose widget renders by this.</summary>
        public string Size { get; set; } = "small";

        public WidgetInfo(string widgetId, string definitionId)
        {
            WidgetId = widgetId;
            DefinitionId = definitionId;
            IsActive = true;
            CustomizationMode = CustomizationState.None;
        }
    }
}
