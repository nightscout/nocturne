using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Controllers;

/// <summary>
/// Metadata controller that exposes type definitions for frontend clients
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class MetadataController : ControllerBase
{
    /// <summary>
    /// Get WebSocket event types metadata
    /// This endpoint exists primarily to ensure NSwag generates TypeScript types for WebSocket events
    /// </summary>
    /// <returns>WebSocket events metadata</returns>
    [HttpGet("websocket-events")]
    [ProducesResponseType(typeof(WebSocketEventsMetadata), 200)]
    public ActionResult<WebSocketEventsMetadata> GetWebSocketEvents()
    {
        return Ok(
            new WebSocketEventsMetadata
            {
                AvailableEvents = Enum.GetValues<WebSocketEvents>(),
                Description = "Available WebSocket event types for real-time communication",
            }
        );
    }

    /// <summary>
    /// Get external URLs for documentation and website
    /// This endpoint provides a single source of truth for all external Nocturne URLs
    /// </summary>
    /// <returns>External URLs configuration</returns>
    [HttpGet("external-urls")]
    [ProducesResponseType(typeof(ExternalUrls), 200)]
    public ActionResult<ExternalUrls> GetExternalUrls()
    {
        return Ok(
            new ExternalUrls
            {
                Website = UrlConstants.External.NocturneWebsite,
                DocsBase = UrlConstants.External.NocturneDocsBase,
                ConnectorDocs = new ConnectorDocsUrls
                {
                    Dexcom = UrlConstants.External.DocsDexcom,
                    Libre = UrlConstants.External.DocsLibre,
                    CareLink = UrlConstants.External.DocsCareLink,
                    Nightscout = UrlConstants.External.DocsNightscout,
                    Glooko = UrlConstants.External.DocsGlooko,
                },
            }
        );
    }
    /// <summary>
    /// Get treatment event types metadata
    /// This endpoint exposes all available treatment event types for type-safe usage in frontend clients
    /// </summary>
    /// <returns>Treatment event types metadata</returns>
    [HttpGet("treatment-event-types")]
    [ProducesResponseType(typeof(TreatmentEventTypesMetadata), 200)]
    public ActionResult<TreatmentEventTypesMetadata> GetTreatmentEventTypes()
    {
        return Ok(
            new TreatmentEventTypesMetadata
            {
                AvailableTypes = Enum.GetValues<TreatmentEventType>(),
                Configurations = EventTypeConfigurations.GetAll(),
                Description = "Available treatment event types for diabetes management events",
            }
        );
    }

    /// <summary>
    /// Get state span types metadata
    /// This endpoint exposes all available state span categories and their states for type-safe usage in frontend clients
    /// </summary>
    /// <returns>State span types metadata</returns>
    [HttpGet("state-span-types")]
    [ProducesResponseType(typeof(StateSpanTypesMetadata), 200)]
    public ActionResult<StateSpanTypesMetadata> GetStateSpanTypes()
    {
        return Ok(
            new StateSpanTypesMetadata
            {
                AvailableCategories = Enum.GetValues<StateSpanCategory>(),
                BasalDeliveryStates = Enum.GetValues<BasalDeliveryState>(),
                BasalDeliveryOrigins = Enum.GetValues<BasalDeliveryOrigin>(),
                PumpModeStates = Enum.GetValues<PumpModeState>(),
                PumpConnectivityStates = Enum.GetValues<PumpConnectivityState>(),
                Description = "Available state span categories and their associated states",
            }
        );
    }

    /// <summary>
    /// Get statistics metadata for type generation
    /// This endpoint exists primarily to ensure NSwag generates TypeScript types for statistics models
    /// </summary>
    /// <returns>Statistics types metadata</returns>
    [HttpGet("statistics-types")]
    [ProducesResponseType(typeof(StatisticsTypesMetadata), 200)]
    public ActionResult<StatisticsTypesMetadata> GetStatisticsTypes()
    {
        return Ok(
            new StatisticsTypesMetadata
            {
                Description = "Statistics types for insulin delivery reports",
            }
        );
    }

    /// <summary>
    /// Get widget definitions metadata
    /// This endpoint provides all available dashboard widget definitions for frontend configuration
    /// </summary>
    /// <returns>Widget definitions metadata</returns>
    [HttpGet("widget-definitions")]
    [ProducesResponseType(typeof(WidgetDefinitionsMetadata), 200)]
    public ActionResult<WidgetDefinitionsMetadata> GetWidgetDefinitions()
    {
        return Ok(
            new WidgetDefinitionsMetadata
            {
                Definitions = GetAllWidgetDefinitions(),
                AvailablePlacements = Enum.GetValues<WidgetPlacement>(),
                AvailableSizes = Enum.GetValues<WidgetSize>(),
                AvailableUICategories = Enum.GetValues<WidgetUICategory>(),
                Description = "Available dashboard widget definitions for configuration",
            }
        );
    }

    private static WidgetDefinition[] GetAllWidgetDefinitions() =>
    [
        // Top widgets (widget grid above the chart)
        new()
        {
            Id = WidgetId.BgDelta,
            Name = "BG Delta",
            Description = "Blood glucose change with connection status and last updated time",
            DefaultEnabled = true,
            Icon = "TrendingUp",
            UICategory = WidgetUICategory.Glucose,
            Placement = WidgetPlacement.Top,
        },
        new()
        {
            Id = WidgetId.LastUpdated,
            Name = "Last Updated",
            Description = "Time since last glucose reading with device info",
            DefaultEnabled = true,
            Icon = "Clock",
            UICategory = WidgetUICategory.Device,
            Placement = WidgetPlacement.Top,
        },
        new()
        {
            Id = WidgetId.ConnectionStatus,
            Name = "Connection Status",
            Description = "Real-time data connection status",
            DefaultEnabled = true,
            Icon = "Wifi",
            UICategory = WidgetUICategory.Status,
            Placement = WidgetPlacement.Top,
        },
        new()
        {
            Id = WidgetId.Meals,
            Name = "Recent Meals",
            Description = "Recent meal entries and carb intake",
            DefaultEnabled = false,
            Icon = "UtensilsCrossed",
            UICategory = WidgetUICategory.Meals,
            Placement = WidgetPlacement.Top,
        },
        new()
        {
            Id = WidgetId.Trackers,
            Name = "Trackers",
            Description = "Active tracker status and progress",
            DefaultEnabled = false,
            Icon = "ListChecks",
            UICategory = WidgetUICategory.Status,
            Placement = WidgetPlacement.Top,
        },
        new()
        {
            Id = WidgetId.TirChart,
            Name = "Time in Range",
            Description = "Stacked chart showing time in glucose ranges",
            DefaultEnabled = false,
            Icon = "BarChart3",
            UICategory = WidgetUICategory.Glucose,
            Placement = WidgetPlacement.Top,
        },
        new()
        {
            Id = WidgetId.DailySummary,
            Name = "Daily Summary",
            Description = "Today's glucose statistics overview",
            DefaultEnabled = false,
            Icon = "CalendarDays",
            UICategory = WidgetUICategory.Glucose,
            Placement = WidgetPlacement.Top,
        },
        new()
        {
            Id = WidgetId.Clock,
            Name = "Clock",
            Description = "Current time and date display",
            DefaultEnabled = false,
            Icon = "Clock",
            UICategory = WidgetUICategory.Status,
            Placement = WidgetPlacement.Top,
        },
        new()
        {
            Id = WidgetId.Tdd,
            Name = "Total Daily Dose",
            Description = "Today's insulin with basal/bolus breakdown",
            DefaultEnabled = true,
            Icon = "PieChart",
            UICategory = WidgetUICategory.Glucose,
            Placement = WidgetPlacement.Top,
        },
        // Main sections (larger dashboard components)
        new()
        {
            Id = WidgetId.GlucoseChart,
            Name = "Glucose Chart",
            Description = "Main glucose trend chart with treatments",
            DefaultEnabled = true,
            Icon = "LineChart",
            UICategory = WidgetUICategory.Glucose,
            Placement = WidgetPlacement.Main,
        },
        new()
        {
            Id = WidgetId.Statistics,
            Name = "Statistics",
            Description = "BG statistics cards",
            DefaultEnabled = true,
            Icon = "BarChart2",
            UICategory = WidgetUICategory.Glucose,
            Placement = WidgetPlacement.Main,
        },
        new()
        {
            Id = WidgetId.Predictions,
            Name = "Predictions",
            Description = "Glucose prediction lines on chart",
            DefaultEnabled = true,
            Icon = "TrendingUp",
            UICategory = WidgetUICategory.Glucose,
            Placement = WidgetPlacement.Main,
        },
        new()
        {
            Id = WidgetId.DailyStats,
            Name = "Daily Stats",
            Description = "Recent entries card",
            DefaultEnabled = true,
            Icon = "CalendarDays",
            UICategory = WidgetUICategory.Glucose,
            Placement = WidgetPlacement.Main,
        },
        new()
        {
            Id = WidgetId.Treatments,
            Name = "Treatments",
            Description = "Recent treatments card",
            DefaultEnabled = true,
            Icon = "Syringe",
            UICategory = WidgetUICategory.Glucose,
            Placement = WidgetPlacement.Main,
        },
        new()
        {
            Id = WidgetId.Agp,
            Name = "AGP",
            Description = "Ambulatory glucose profile",
            DefaultEnabled = false,
            Icon = "Activity",
            UICategory = WidgetUICategory.Glucose,
            Placement = WidgetPlacement.Main,
        },
        new()
        {
            Id = WidgetId.BatteryStatus,
            Name = "Battery Status",
            Description = "Device battery status",
            DefaultEnabled = true,
            Icon = "Battery",
            UICategory = WidgetUICategory.Device,
            Placement = WidgetPlacement.Main,
        },
    ];
}

/// <summary>
/// Metadata about available WebSocket events
/// </summary>
public class WebSocketEventsMetadata
{
    /// <summary>
    /// Array of all available WebSocket event types
    /// </summary>
    public WebSocketEvents[] AvailableEvents { get; set; } = [];

    /// <summary>
    /// Description of the WebSocket events
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Metadata about available treatment event types
/// </summary>
public class TreatmentEventTypesMetadata
{
    /// <summary>
    /// Array of all available treatment event types
    /// </summary>
    public TreatmentEventType[] AvailableTypes { get; set; } = [];

    /// <summary>
    /// Full configurations for each event type including field applicability
    /// </summary>
    public EventTypeConfiguration[] Configurations { get; set; } = [];

    /// <summary>
    /// Description of the treatment event types
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Metadata about available widget definitions
/// </summary>
public class WidgetDefinitionsMetadata
{
    /// <summary>
    /// Array of all widget definitions with full metadata
    /// </summary>
    public WidgetDefinition[] Definitions { get; set; } = [];

    /// <summary>
    /// All available placement options
    /// </summary>
    public WidgetPlacement[] AvailablePlacements { get; set; } = [];

    /// <summary>
    /// All available size options
    /// </summary>
    public WidgetSize[] AvailableSizes { get; set; } = [];

    /// <summary>
    /// All available UI category options
    /// </summary>
    public WidgetUICategory[] AvailableUICategories { get; set; } = [];

    /// <summary>
    /// Description of the widget definitions
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Metadata about state span types for NSwag generation
/// </summary>
public class StateSpanTypesMetadata
{
    /// <summary>
    /// Array of all available state span categories
    /// </summary>
    public StateSpanCategory[] AvailableCategories { get; set; } = [];

    /// <summary>
    /// Array of all basal delivery states
    /// </summary>
    public BasalDeliveryState[] BasalDeliveryStates { get; set; } = [];

    /// <summary>
    /// Array of all basal delivery origin values
    /// </summary>
    public BasalDeliveryOrigin[] BasalDeliveryOrigins { get; set; } = [];

    /// <summary>
    /// Array of all pump mode states
    /// </summary>
    public PumpModeState[] PumpModeStates { get; set; } = [];

    /// <summary>
    /// Array of all pump connectivity states
    /// </summary>
    public PumpConnectivityState[] PumpConnectivityStates { get; set; } = [];

    /// <summary>
    /// Description of the state span types
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Metadata about statistics types for NSwag generation
/// </summary>
public class StatisticsTypesMetadata
{
    /// <summary>
    /// Description of the statistics types
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Sample basal analysis response (for type generation)
    /// </summary>
    public BasalAnalysisResponse? SampleBasalAnalysis { get; set; }

    /// <summary>
    /// Sample daily basal/bolus ratio response (for type generation)
    /// </summary>
    public DailyBasalBolusRatioResponse? SampleDailyBasalBolusRatio { get; set; }

    /// <summary>
    /// Sample hourly basal percentile data (for type generation)
    /// </summary>
    public HourlyBasalPercentileData? SampleHourlyPercentile { get; set; }

    /// <summary>
    /// Sample daily basal/bolus ratio data (for type generation)
    /// </summary>
    public DailyBasalBolusRatioData? SampleDailyData { get; set; }

    /// <summary>
    /// Sample insulin delivery statistics (for type generation)
    /// </summary>
    public InsulinDeliveryStatistics? SampleInsulinDelivery { get; set; }
}

