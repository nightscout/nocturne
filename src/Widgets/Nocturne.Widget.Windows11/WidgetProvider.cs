using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Windows.Widgets.Providers;

namespace Nocturne.Widget.Windows11;

/// <summary>
/// Implements the Windows 11 Widget provider interface for Nocturne
/// </summary>
[ComVisible(true)]
[ComDefaultInterface(typeof(IWidgetProvider))]
[Guid("B8E3F2A1-5C4D-4E6F-8A9B-1C2D3E4F5A6B")]
// Removed [ClassInterface(ClassInterfaceType.None)] to match Microsoft sample
public sealed class NocturneWidgetProvider : IWidgetProvider
{
    private readonly Dictionary<string, WidgetInfo> _activeWidgets = new();
    private readonly Dictionary<string, string> _templateCache = new();
    private readonly object _widgetLock = new();

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

        Console.WriteLine($"Action invoked on widget {widgetId}: {verb}");

        switch (verb)
        {
            case "refresh":
                UpdateWidget(widgetId);
                break;

            case "openApp":
                HandleOpenAppAction(data);
                break;

            default:
                Console.WriteLine($"Unknown action verb: {verb}");
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

    private void UpdateWidget(string widgetId)
    {
        try
        {
            WidgetInfo? widgetInfo;
            lock (_widgetLock)
            {
                if (!_activeWidgets.TryGetValue(widgetId, out widgetInfo))
                {
                    Console.WriteLine($"Widget not found: {widgetId}");
                    return;
                }
            }

            // Super simple template like Microsoft sample
            var template = """
                {
                    "type": "AdaptiveCard",
                    "body": [
                        {
                            "type": "TextBlock",
                            "text": "${glucose} ${direction}"
                        }
                    ],
                    "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                    "version": "1.5"
                }
                """;

            // Use JsonObject like Microsoft sample
            var dataNode = new JsonObject
            {
                ["glucose"] = "120",
                ["direction"] = "→"
            };

            var updateOptions = new WidgetUpdateRequestOptions(widgetId)
            {
                Template = template,
                Data = dataNode.ToJsonString(),
                CustomState = widgetInfo.CustomState ?? string.Empty,
            };

            Console.WriteLine($"Updating widget {widgetId} with template and data");
            WidgetManager.GetDefault().UpdateWidget(updateOptions);
            Console.WriteLine($"Widget {widgetId} updated successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to update widget {widgetId}: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
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
        Console.WriteLine($"Open app action with data: {data}");

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
            Console.WriteLine($"Failed to launch Nocturne app: {ex.Message}");
        }
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

        public WidgetInfo(string widgetId, string definitionId)
        {
            WidgetId = widgetId;
            DefinitionId = definitionId;
            IsActive = true;
        }
    }
}
