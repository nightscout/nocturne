using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Configuration;
using Nocturne.API.Models.OAuth;
using Nocturne.API.Multitenancy;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.Services;

namespace Nocturne.API.Controllers;

/// <summary>
/// Metadata controller that exposes type definitions for NSwag and the frontend client generation pipeline.
/// </summary>
/// <remarks>
/// This controller exists solely to ensure NSwag generates TypeScript types for models that are not
/// otherwise reachable through regular API endpoints (e.g., WebSocket event envelopes, connector
/// configuration shapes, OAuth scope lists). It is excluded from the interactive API explorer via
/// <see cref="ApiExplorerSettingsAttribute"/> with <c>IgnoreApi = true</c>.
///
/// None of the endpoints here perform real business logic — they return empty/stub responses.
/// All endpoints are permitted during initial setup (<see cref="AllowDuringSetupAttribute"/>).
/// </remarks>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/[controller]")]
[Tags("Metadata")]
[AllowDuringSetup]
public class MetadataController : ControllerBase
{
    /// <summary>
    /// Get WebSocket event types metadata
    /// This endpoint exists primarily to ensure NSwag generates TypeScript types for WebSocket events
    /// </summary>
    /// <returns>WebSocket events metadata</returns>
    [HttpGet("websocket-events")]
    [RemoteQuery]
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
    [RemoteQuery]
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
    [RemoteQuery]
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
    [RemoteQuery]
    [ProducesResponseType(typeof(StateSpanTypesMetadata), 200)]
    public ActionResult<StateSpanTypesMetadata> GetStateSpanTypes()
    {
        return Ok(
            new StateSpanTypesMetadata
            {
                AvailableCategories = Enum.GetValues<StateSpanCategory>(),
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
    [RemoteQuery]
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
    /// Get connector property keys metadata
    /// This endpoint exposes all available connector property keys for type-safe usage in frontend clients
    /// </summary>
    /// <returns>Connector property keys metadata</returns>
    [HttpGet("connector-property-keys")]
    [RemoteQuery]
    [ProducesResponseType(typeof(ConnectorPropertyKeysMetadata), 200)]
    public ActionResult<ConnectorPropertyKeysMetadata> GetConnectorPropertyKeys()
    {
        return Ok(
            new ConnectorPropertyKeysMetadata
            {
                AvailableKeys = Enum.GetValues<ConnectorPropertyKey>(),
                Description = "Available connector configuration property keys",
            }
        );
    }

    /// <summary>
    /// Get widget definitions metadata
    /// This endpoint provides all available dashboard widget definitions for frontend configuration
    /// </summary>
    /// <returns>Widget definitions metadata</returns>
    [HttpGet("widget-definitions")]
    [RemoteQuery]
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

    /// <summary>
    /// Get multitenancy configuration for the frontend
    /// Provides details needed for tenant switching and display
    /// </summary>
    [HttpGet("multitenancy")]
    [RemoteQuery]
    [ProducesResponseType(typeof(MultitenancyInfo), 200)]
    public ActionResult<MultitenancyInfo> GetMultitenancyInfo(
        [FromServices] IOptions<MultitenancyConfiguration> config,
        [FromServices] IOptions<OperatorConfiguration> operatorConfig,
        [FromServices] ITenantAccessor tenantAccessor)
    {
        var tenantContext = tenantAccessor.Context;

        return Ok(new MultitenancyInfo
        {
            BaseDomain = config.Value.BaseDomain,
            SubdomainResolution = true,
            AllowSelfServiceCreation = operatorConfig.Value.AllowSelfServiceCreation,
            CurrentTenantSlug = tenantContext?.Slug,
            CurrentTenantId = tenantContext?.TenantId,
            CurrentTenantDisplayName = tenantContext?.DisplayName,
        });
    }

    /// <summary>
    /// Get data source categories metadata
    /// This endpoint ensures NSwag generates TypeScript types for DataSourceCategory
    /// </summary>
    /// <returns>Data source categories metadata</returns>
    [HttpGet("data-source-categories")]
    [RemoteQuery]
    [ProducesResponseType(typeof(DataSourceCategoriesMetadata), 200)]
    public ActionResult<DataSourceCategoriesMetadata> GetDataSourceCategories()
    {
        return Ok(
            new DataSourceCategoriesMetadata
            {
                AvailableCategories = Enum.GetValues<DataSourceCategory>(),
                Description = "Available data source categories",
            }
        );
    }

    /// <summary>
    /// Get the alert condition tree shape. Exists solely so NSwag generates TypeScript
    /// interfaces for <see cref="ConditionNode"/> and every condition payload record
    /// — they're stored as opaque JSON on the rule entity and not otherwise reachable
    /// through a controller signature.
    /// </summary>
    [HttpGet("alert-condition-types")]
    [RemoteQuery]
    [ApiExplorerSettings(IgnoreApi = false)]
    [ProducesResponseType(typeof(AlertConditionTypesMetadata), 200)]
    public ActionResult<AlertConditionTypesMetadata> GetAlertConditionTypes()
    {
        return Ok(new AlertConditionTypesMetadata
        {
            Sample = new ConditionNode("threshold"),
            TempBasalMetrics = Enum.GetValues<TempBasalMetric>(),
            Description = "Polymorphic ConditionNode shape used by alert rules.",
        });
    }

    /// <summary>
    /// Get authentication error codes metadata
    /// This endpoint ensures NSwag generates TypeScript types for AuthErrorCode
    /// </summary>
    /// <returns>Auth error codes metadata</returns>
    [HttpGet("auth-error-codes")]
    [RemoteQuery]
    [ApiExplorerSettings(IgnoreApi = false)]
    [ProducesResponseType(typeof(AuthErrorCodesMetadata), 200)]
    public ActionResult<AuthErrorCodesMetadata> GetAuthErrorCodes()
    {
        return Ok(
            new AuthErrorCodesMetadata
            {
                AvailableCodes = Enum.GetValues<AuthErrorCode>(),
                Description = "Authentication error codes returned by the auth flow",
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

/// <summary>
/// Metadata about connector property keys for NSwag generation
/// </summary>
public class ConnectorPropertyKeysMetadata
{
    /// <summary>
    /// Array of all available connector property keys
    /// </summary>
    public ConnectorPropertyKey[] AvailableKeys { get; set; } = [];

    /// <summary>
    /// Description of the connector property keys
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Multitenancy configuration exposed to the frontend
/// </summary>
public class MultitenancyInfo
{
    /// <summary>
    /// Base domain for subdomain-based tenant resolution (e.g. "nocturnecgm.com")
    /// </summary>
    public string? BaseDomain { get; set; }

    /// <summary>
    /// Whether subdomain-based tenant resolution is active
    /// </summary>
    public bool SubdomainResolution { get; set; }

    /// <summary>
    /// Slug of the tenant resolved for the current request
    /// </summary>
    public string? CurrentTenantSlug { get; set; }

    /// <summary>
    /// ID of the tenant resolved for the current request
    /// </summary>
    public Guid? CurrentTenantId { get; set; }

    /// <summary>
    /// Display name of the tenant resolved for the current request
    /// </summary>
    public string? CurrentTenantDisplayName { get; set; }

    /// <summary>
    /// Whether self-service tenant creation is allowed
    /// </summary>
    public bool AllowSelfServiceCreation { get; set; }
}

/// <summary>
/// Metadata about data source categories for NSwag generation
/// </summary>
public class DataSourceCategoriesMetadata
{
    /// <summary>
    /// Array of all available data source categories
    /// </summary>
    public DataSourceCategory[] AvailableCategories { get; set; } = [];

    /// <summary>
    /// Description of the data source categories
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Forces NSwag to emit TypeScript interfaces for <see cref="ConditionNode"/> and
/// every condition payload record — they're stored as opaque JSON on the rule entity
/// and otherwise never appear in a controller signature.
/// </summary>
public class AlertConditionTypesMetadata
{
    /// <summary>A sample <see cref="ConditionNode"/>; pulls every sub-record into the OpenAPI schema.</summary>
    public ConditionNode? Sample { get; set; }

    /// <summary>All <see cref="TempBasalMetric"/> values.</summary>
    public TempBasalMetric[] TempBasalMetrics { get; set; } = [];

    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Metadata about authentication error codes for NSwag generation
/// </summary>
public class AuthErrorCodesMetadata
{
    /// <summary>
    /// Array of all available authentication error codes
    /// </summary>
    public AuthErrorCode[] AvailableCodes { get; set; } = [];

    /// <summary>
    /// Description of the auth error codes
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
