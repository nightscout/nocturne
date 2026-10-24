using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Models.OAuth;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Controllers;

/// <summary>
/// Metadata controller that exposes type definitions for NSwag and the frontend client generation pipeline.
/// </summary>
/// <remarks>
/// Most of these endpoints exist only to pull types into the generated client that no other endpoint
/// reaches, and answer with a constant, usually an enum's values. Widget definitions serve real data:
/// the widget catalogue.
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
