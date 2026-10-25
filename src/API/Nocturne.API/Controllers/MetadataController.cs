using Microsoft.AspNetCore.Authorization;
using System.Reflection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Core.Constants;
using Nocturne.API.Services.ChartData;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Services;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Models.OAuth;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers;

/// <summary>
/// Metadata controller that exposes type definitions for NSwag and the frontend client generation pipeline.
/// </summary>
/// <remarks>
/// Most of these endpoints exist only to pull types into the generated client that no other endpoint
/// reaches — WebSocket event envelopes, connector configuration shapes, OAuth scope lists — and
/// answer with a constant, usually an enum's values. Several serve real data: help references return shared application limits, widget definitions return
/// the widget catalogue, and multitenancy answers from configuration and the resolved tenant.
///
/// The class hides itself from the interactive API explorer with
/// <see cref="ApiExplorerSettingsAttribute"/>, so an endpoint whose types must reach the generated
/// client has to override that with <c>IgnoreApi = false</c> to reach the OpenAPI document at all.
/// All endpoints are permitted during initial setup (<see cref="AllowDuringSetupAttribute"/>).
/// </remarks>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/[controller]")]
[Tags("Metadata")]
[AllowDuringSetup]
[AllowAnonymous]
public class MetadataController : ControllerBase
{
    /// <summary>Get the same reference limits and import capabilities used by Nocturne.</summary>
    [HttpGet("help-reference")]
    [ApiExplorerSettings(IgnoreApi = false)]
    [RemoteQuery]
    [ProducesResponseType(typeof(HelpReferenceFacts), 200)]
    public ActionResult<HelpReferenceFacts> GetHelpReference()
    {
        var google = new GoogleHealthConnectorConfiguration();
        var history = typeof(GoogleHealthConnectorConfiguration)
            .GetProperty(nameof(GoogleHealthConnectorConfiguration.HistoryDays))!
            .GetCustomAttribute<ConnectorPropertyAttribute>()!;
        return Ok(new HelpReferenceFacts
        {
            ReportBands = new GlycemicThresholds(),
            ChartBands = ChartThresholdsBuilder.ReferenceBands(),
            MgdlPerMmol = GlucoseConstants.MgdlPerMmol,
            MaximumPlausibleGlucose = GlucoseConstants.MaxPlausibleMgdl,
            GoogleHealthCallbackPath = GoogleHealthConnectorConfiguration.CallbackPath,
            GoogleHealthSyncIntervalMinutes = google.SyncIntervalMinutes,
            GoogleHealthDefaultHistoryDays = google.HistoryDays,
            GoogleHealthMinimumHistoryDays = history.MinValue,
            GoogleHealthMaximumHistoryDays = history.MaxValue,
            GoogleHealthReadScopes = GoogleHealthClient.SupportedTypes.Select(GoogleHealthClient.ScopeFor).Distinct().ToArray(),
            GoogleHealthCapabilities = GoogleHealthClient.Capabilities,
        });
    }

    /// <summary>
    /// Get widget definitions metadata
    /// This endpoint provides all available dashboard widget definitions for frontend configuration
    /// </summary>
    /// <returns>Widget definitions metadata</returns>
    [HttpGet("widget-definitions")]
    [RemoteQuery]
    [ApiExplorerSettings(IgnoreApi = false)]
    [ProducesResponseType(typeof(WidgetDefinitionsMetadata), 200)]
    public ActionResult<WidgetDefinitionsMetadata> GetWidgetDefinitions()
    {
        return Ok(
            new WidgetDefinitionsMetadata
            {
                Definitions = [.. WidgetCatalog.All],
                Description = "Available dashboard widget definitions for configuration",
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
            ValidationIssue = new RustValidationIssue("condition", "composite", "list_empty", "conditions"),
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

    /// <summary>
    /// Get tracker realtime actions metadata
    /// This endpoint ensures NSwag generates TypeScript types for TrackerRealtimeAction, the
    /// <c>action</c> of every <c>trackerUpdate</c> event, which the web app validates against
    /// </summary>
    /// <returns>Tracker realtime actions metadata</returns>
    [HttpGet("tracker-realtime-actions")]
    [RemoteQuery]
    [ApiExplorerSettings(IgnoreApi = false)]
    [ProducesResponseType(typeof(TrackerRealtimeActionsMetadata), 200)]
    public ActionResult<TrackerRealtimeActionsMetadata> GetTrackerRealtimeActions()
    {
        return Ok(
            new TrackerRealtimeActionsMetadata
            {
                AvailableActions = Enum.GetValues<TrackerRealtimeAction>(),
                Description = "Actions a trackerUpdate realtime event can carry",
            }
        );
    }

    /// <summary>
    /// Exposes the phases a connector sync reports in the <c>phase</c> of every <c>syncProgress</c>
    /// event, which the web app validates against
    /// </summary>
    /// <returns>Sync phase metadata</returns>
    [HttpGet("sync-phases")]
    [RemoteQuery]
    [ApiExplorerSettings(IgnoreApi = false)]
    [ProducesResponseType(typeof(SyncPhasesMetadata), 200)]
    public ActionResult<SyncPhasesMetadata> GetSyncPhases()
    {
        return Ok(
            new SyncPhasesMetadata
            {
                AvailablePhases = Enum.GetValues<SyncPhase>(),
                Description = "Phases a syncProgress realtime event can carry",
            }
        );
    }

    /// <summary>
    /// Get the Home Assistant hub's message shapes. Exists so the OpenAPI document, and every SDK
    /// generated from it, carries the payload of each message <c>/hubs/home-assistant</c> sends.
    /// </summary>
    [HttpGet("home-assistant-hub-messages")]
    [ApiExplorerSettings(IgnoreApi = false)]
    [ProducesResponseType(typeof(HomeAssistantHubMessagesMetadata), 200)]
    public ActionResult<HomeAssistantHubMessagesMetadata> GetHomeAssistantHubMessages()
    {
        return Ok(
            new HomeAssistantHubMessagesMetadata
            {
                AcknowledgeOutcomes = Enum.GetValues<AlertAcknowledgementOutcome>(),
                Description = "Payloads of the messages the Home Assistant hub sends, keyed by message name",
            }
        );
    }

    /// <summary>
    /// Get TOTP setup failure codes metadata
    /// This endpoint ensures NSwag generates TypeScript types for TotpSetupFailure, which the
    /// verify-setup endpoint returns as the <c>detail</c> of its 400 and the web app turns into copy
    /// </summary>
    /// <returns>TOTP setup failure codes metadata</returns>
    [HttpGet("totp-setup-failures")]
    [RemoteQuery]
    [ApiExplorerSettings(IgnoreApi = false)]
    [ProducesResponseType(typeof(TotpSetupFailuresMetadata), 200)]
    public ActionResult<TotpSetupFailuresMetadata> GetTotpSetupFailures()
    {
        return Ok(
            new TotpSetupFailuresMetadata
            {
                AvailableFailures = Enum.GetValues<TotpSetupFailure>(),
                Description = "Reasons a TOTP setup attempt can be refused",
            }
        );
    }

    /// <summary>
    /// Get permission atoms metadata
    /// This endpoint ensures NSwag generates a TypeScript enum for PermissionAtom, which the
    /// permission selector is checked against
    /// </summary>
    /// <returns>Permission atoms metadata</returns>
    [HttpGet("permission-atoms")]
    [RemoteQuery]
    [ApiExplorerSettings(IgnoreApi = false)]
    [ProducesResponseType(typeof(PermissionAtomsMetadata), 200)]
    public ActionResult<PermissionAtomsMetadata> GetPermissionAtoms()
    {
        return Ok(
            new PermissionAtomsMetadata
            {
                AvailableAtoms = Enum.GetValues<PermissionAtom>(),
                Description = "Atoms a tenant role, invite or member permission may confer",
            }
        );
    }
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
    /// Description of the widget definitions
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

    /// <summary>A sample <see cref="RustValidationIssue"/>; pulls the type into the generated client.</summary>
    public RustValidationIssue? ValidationIssue { get; set; }

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

/// <summary>
/// The messages <see cref="Hubs.HomeAssistantHub"/> sends, one property per message, named for it.
/// Each property is null in the response; it exists for its type.
/// </summary>
public class HomeAssistantHubMessagesMetadata
{
    /// <summary>
    /// <c>glucose_reading</c>, to an instance whose credential may read glucose: a live sensor glucose
    /// write on the canonical stream.
    /// </summary>
    public GlucoseReadingEvent? GlucoseReading { get; set; }

    /// <summary>
    /// <c>alert_dispatch</c>, to the instance an alert is addressed to: an alert fired, or a delivery
    /// that failed while the instance was offline, replayed on <c>Subscribe</c>.
    /// </summary>
    public AlertPayload? AlertDispatch { get; set; }

    /// <summary><c>alert_resolved</c>, to every instance of the tenant: an excursion closed.</summary>
    public AlertResolvedEvent? AlertResolved { get; set; }

    /// <summary>
    /// <c>alert_acknowledged</c>, to every instance of the tenant: excursions were acknowledged for everyone.
    /// </summary>
    public AlertAcknowledgedEvent? AlertAcknowledged { get; set; }

    /// <summary>All values the hub's <c>Acknowledge</c> method can return.</summary>
    public AlertAcknowledgementOutcome[] AcknowledgeOutcomes { get; set; } = [];

    /// <summary>
    /// Description of the messages
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Metadata about TOTP setup failure codes for NSwag generation
/// </summary>
public class TotpSetupFailuresMetadata
{
    /// <summary>
    /// Array of all reasons a TOTP setup attempt can be refused
    /// </summary>
    public TotpSetupFailure[] AvailableFailures { get; set; } = [];

    /// <summary>
    /// Description of the TOTP setup failure codes
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Metadata about permission atoms for NSwag generation
/// </summary>
public class PermissionAtomsMetadata
{
    /// <summary>
    /// Array of all atoms a tenant role, invite or member permission may confer
    /// </summary>
    public PermissionAtom[] AvailableAtoms { get; set; } = [];

    /// <summary>
    /// Description of the permission atoms
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Metadata about tracker realtime actions for NSwag generation
/// </summary>
public class TrackerRealtimeActionsMetadata
{
    /// <summary>
    /// Array of all actions a tracker update event can carry
    /// </summary>
    public TrackerRealtimeAction[] AvailableActions { get; set; } = [];

    /// <summary>
    /// Description of the tracker realtime actions
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Metadata about connector sync phases for NSwag generation
/// </summary>
public class SyncPhasesMetadata
{
    /// <summary>
    /// Array of all phases a sync progress event can carry
    /// </summary>
    public SyncPhase[] AvailablePhases { get; set; } = [];

    /// <summary>
    /// Description of the sync phases
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
