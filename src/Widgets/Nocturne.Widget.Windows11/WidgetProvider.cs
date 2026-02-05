using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.Widgets.Providers;
using Nocturne.Widget.Contracts;

namespace Nocturne.Widget.Windows11;

/// <summary>
/// Implements the Windows 11 Widget provider interface for Nocturne
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
    private readonly ILogger<NocturneWidgetProvider> _logger;

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
    /// Initializes a new instance of the NocturneWidgetProvider.
    /// Required parameterless constructor for COM activation.
    /// NOTE: Keep constructor minimal - heavy initialization blocks DCOM registration.
    /// </summary>
    public NocturneWidgetProvider()
    {
        Console.WriteLine("NocturneWidgetProvider initialized");

        // Resolve services from the static service provider
        _credentialStore = Program.Services.GetRequiredService<ICredentialStore>();
        _apiClient = Program.Services.GetRequiredService<INocturneApiClient>();
        _logger = Program.Services.GetRequiredService<ILogger<NocturneWidgetProvider>>();

        // Don't call RecoverRunningWidgets here - it blocks DCOM registration
        // Existing widgets will be handled on first Activate/Create call
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

        // Send initial content
        UpdateWidget(widgetId);
    }

    /// <inheritdoc />
    public void DeleteWidget(string widgetId, string customState)
    {
        Console.WriteLine($"Deleting widget: {widgetId}");

        lock (_widgetLock)
        {
            _activeWidgets.Remove(widgetId);

            // Signal to exit if no widgets remain
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

            case "saveConfig":
                HandleSaveConfigAction(widgetId, data);
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

        // Refresh the widget with the new context
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

        // Refresh data when widget becomes visible
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
                widgetInfo.InCustomization = true;
                UpdateWidget(widgetId);
            }
        }
    }

    private void UpdateWidget(string widgetId)
    {
        // Fire and forget async update
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

            // Check if we're in customization mode
            if (widgetInfo.InCustomization)
            {
                // Show customization card with input fields
                var credentials = await _credentialStore.GetCredentialsAsync();
                template = GetCustomizationTemplate();
                dataNode = new JsonObject
                {
                    ["apiUrl"] = credentials?.ApiUrl ?? "",
                    ["token"] = credentials?.Token ?? ""
                };
                _logger.LogInformation("Showing customization card for widget {WidgetId}", widgetId);
            }
            else
            {
                // Check if we have credentials configured
                var hasCredentials = await _credentialStore.HasCredentialsAsync();

                if (!hasCredentials)
                {
                    // Show setup card
                    template = GetSetupTemplate();
                    dataNode = new JsonObject();
                    _logger.LogInformation("Showing setup card for widget {WidgetId}", widgetId);
                }
                else
                {
                    // Fetch data from API
                    var summary = await _apiClient.GetSummaryAsync(hours: 0, includePredictions: false);

                    if (summary is null)
                    {
                        // Show error card
                        template = GetErrorTemplate();
                        dataNode = new JsonObject
                        {
                            ["errorMessage"] = "Unable to connect to Nocturne server"
                        };
                        _logger.LogWarning("Failed to fetch data for widget {WidgetId}", widgetId);
                    }
                    else
                    {
                        // Show glucose card with real data
                        template = GetGlucoseTemplate(widgetInfo.DefinitionId);
                        dataNode = CreateGlucoseData(summary);
                        _logger.LogDebug("Updated widget {WidgetId} with glucose: {Glucose}",
                            widgetId, summary.CurrentGlucose);
                    }
                }
            }

            var updateOptions = new WidgetUpdateRequestOptions(widgetId)
            {
                Template = template,
                Data = dataNode.ToJsonString(),
                CustomState = widgetInfo.CustomState ?? string.Empty,
            };

            _logger.LogDebug("Sending update to widget {WidgetId}", widgetId);
            WidgetManager.GetDefault().UpdateWidget(updateOptions);
            _logger.LogDebug("Widget {WidgetId} updated successfully", widgetId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update widget {WidgetId}", widgetId);
        }
    }

    private static string GetCustomizationTemplate()
    {
        return """
            {
                "type": "AdaptiveCard",
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "version": "1.5",
                "body": [
                    {
                        "type": "TextBlock",
                        "text": "Configure Nocturne",
                        "size": "Medium",
                        "weight": "Bolder"
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
                        "type": "Input.Text",
                        "id": "token",
                        "label": "API Token",
                        "placeholder": "Your API secret or token",
                        "value": "${token}",
                        "style": "password",
                        "isRequired": true
                    }
                ],
                "actions": [
                    {
                        "type": "Action.Execute",
                        "title": "Save",
                        "verb": "saveConfig"
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
                                "text": "Click the ... menu and select Customize to configure",
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
                    },
                    {
                        "type": "Action.Execute",
                        "title": "Configure",
                        "verb": "configure"
                    }
                ]
            }
            """;
    }

    private static string GetGlucoseTemplate(string definitionId)
    {
        // Return appropriate template based on widget size
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
                                "text": "${glucose}",
                                "size": "ExtraLarge",
                                "weight": "Bolder",
                                "horizontalAlignment": "Center"
                            },
                            {
                                "type": "TextBlock",
                                "text": "${direction}",
                                "size": "Large",
                                "horizontalAlignment": "Center",
                                "spacing": "None"
                            },
                            {
                                "type": "TextBlock",
                                "text": "${delta}",
                                "size": "Small",
                                "horizontalAlignment": "Center",
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
                        "title": "Refresh",
                        "verb": "refresh"
                    }
                ],
                "selectAction": {
                    "type": "Action.Execute",
                    "verb": "openApp"
                }
            }
            """;
    }

    private static JsonObject CreateGlucoseData(V4SummaryResponse summary)
    {
        var glucose = summary.CurrentGlucose?.ToString() ?? "---";
        var direction = GetDirectionArrow(summary.Direction);
        var delta = FormatDelta(summary.Delta);

        return new JsonObject
        {
            ["glucose"] = glucose,
            ["direction"] = direction,
            ["delta"] = delta
        };
    }

    private static string FormatDelta(double? delta)
    {
        if (delta is null) return "";
        var sign = delta >= 0 ? "+" : "";
        return $"{sign}{delta:F1}";
    }

    private string GetTemplate(string definitionId)
    {
        // Always use fallback template for now to avoid file path issues
        Console.WriteLine($"Using fallback template for {definitionId}");
        return GetFallbackTemplate(definitionId);
    }

    private static string GetFallbackTemplate(string definitionId)
    {
        // Minimal fallback Adaptive Card template
        return """
            {
                "type": "AdaptiveCard",
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "version": "1.5",
                "body": [
                    {
                        "type": "TextBlock",
                        "text": "${glucose}",
                        "size": "ExtraLarge",
                        "weight": "Bolder",
                        "horizontalAlignment": "Center"
                    },
                    {
                        "type": "TextBlock",
                        "text": "${direction}",
                        "size": "Large",
                        "horizontalAlignment": "Center"
                    }
                ]
            }
            """;
    }

    private static Dictionary<string, object?> CreateSampleCardData(string definitionId)
    {
        // Sample data for testing - will be replaced with real API data
        var data = new Dictionary<string, object?>
        {
            ["glucose"] = "120",
            ["direction"] = "\u2192", // Right arrow (flat)
            ["delta"] = "+2.5",
            ["lastUpdate"] = "2m ago",
            ["iob"] = "1.5",
            ["cob"] = "25",
            ["glucoseColor"] = "#00AA00", // Green for in range
            ["stale"] = false,
        };

        return data;
    }

    private static string GetDirectionArrow(string? direction)
    {
        return direction?.ToUpperInvariant() switch
        {
            "DOUBLEUP" or "DOUBLE_UP" => "\u21C8",        // Double up arrow
            "SINGLEUP" or "SINGLE_UP" or "UP" => "\u2191", // Up arrow
            "FORTYFIVEUP" or "FORTY_FIVE_UP" => "\u2197",  // Diagonal up-right
            "FLAT" => "\u2192",                             // Right arrow (flat)
            "FORTYFIVEDOWN" or "FORTY_FIVE_DOWN" => "\u2198", // Diagonal down-right
            "SINGLEDOWN" or "SINGLE_DOWN" or "DOWN" => "\u2193", // Down arrow
            "DOUBLEDOWN" or "DOUBLE_DOWN" => "\u21CA",     // Double down arrow
            "NOT_COMPUTABLE" or "NONE" or null => "?",
            _ => direction ?? "?"
        };
    }

    private void HandleOpenAppAction(string data)
    {
        _logger.LogInformation("Open app action with data: {Data}", data);

        // Launch the Nocturne app via protocol activation
        try
        {
            var uri = string.IsNullOrEmpty(data)
                ? "nocturne://"
                : $"nocturne://{data}";

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

    private void HandleSaveConfigAction(string widgetId, string data)
    {
        _logger.LogInformation("Saving configuration for widget {WidgetId}", widgetId);

        // Parse the form data from the action
        // The data comes as JSON with the input values
        try
        {
            var formData = JsonSerializer.Deserialize<JsonElement>(data);
            var apiUrl = formData.TryGetProperty("apiUrl", out var urlProp) ? urlProp.GetString() : null;
            var token = formData.TryGetProperty("token", out var tokenProp) ? tokenProp.GetString() : null;

            if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("Invalid configuration: missing apiUrl or token");
                return;
            }

            // Save credentials
            _ = SaveCredentialsAndRefreshAsync(widgetId, apiUrl, token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse configuration data");
        }
    }

    private async Task SaveCredentialsAndRefreshAsync(string widgetId, string apiUrl, string token)
    {
        try
        {
            var credentials = new NocturneCredentials(apiUrl, token);
            await _credentialStore.SaveCredentialsAsync(credentials);
            _logger.LogInformation("Credentials saved successfully");

            // Exit customization mode and refresh widget
            lock (_widgetLock)
            {
                if (_activeWidgets.TryGetValue(widgetId, out var widgetInfo))
                {
                    widgetInfo.InCustomization = false;
                }
            }

            UpdateWidget(widgetId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save credentials");
        }
    }

    private void HandleExitCustomizationAction(string widgetId)
    {
        _logger.LogInformation("Exiting customization for widget {WidgetId}", widgetId);

        lock (_widgetLock)
        {
            if (_activeWidgets.TryGetValue(widgetId, out var widgetInfo))
            {
                widgetInfo.InCustomization = false;
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
        public bool InCustomization { get; set; }

        public WidgetInfo(string widgetId, string definitionId)
        {
            WidgetId = widgetId;
            DefinitionId = definitionId;
            IsActive = true;
            InCustomization = false;
        }
    }
}
