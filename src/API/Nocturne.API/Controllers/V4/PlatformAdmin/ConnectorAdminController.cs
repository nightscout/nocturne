using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Models;

namespace Nocturne.API.Controllers.V4.PlatformAdmin;

/// <summary>
/// Platform-admin controller for cross-tenant connector operations.
/// </summary>
/// <remarks>
/// The per-tenant connector endpoints (<c>/api/v4/services/connectors/...</c>) only act on the
/// caller's own tenant context. After fixing an upstream connector bug, an operator needs to push
/// corrected/missing historical data to affected tenants — across every connector, for any tenant.
/// These endpoints set the tenant context to a target tenant on the admin's behalf and reuse the
/// existing sync path. Restricted to users with the <c>platform_admin</c> role.
/// </remarks>
/// <seealso cref="IConnectorCursorResetService"/>
[ApiController]
[Tags("PlatformAdmin")]
[Route("api/v4/admin/connectors")]
[Produces("application/json")]
[Authorize(Roles = "platform_admin")]
public class ConnectorAdminController : ControllerBase
{
    private readonly IConnectorCursorResetService _cursorResetService;
    private readonly ILogger<ConnectorAdminController> _logger;

    /// <summary>Initializes a new instance of <see cref="ConnectorAdminController"/>.</summary>
    public ConnectorAdminController(
        IConnectorCursorResetService cursorResetService,
        ILogger<ConnectorAdminController> logger)
    {
        _cursorResetService = cursorResetService;
        _logger = logger;
    }

    /// <summary>
    /// List the connectors a target tenant has configured, with their last-sync and health state.
    /// </summary>
    /// <remarks>
    /// Lets an admin inspect the data gap (last successful sync, health, last error) before deciding
    /// to reset cursors.
    /// </remarks>
    /// <param name="tenantId">The target tenant.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{tenantId:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TenantConnectorsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantConnectorsDto>> GetTenantConnectors(
        Guid tenantId, CancellationToken ct)
    {
        var result = await _cursorResetService.GetTenantConnectorsAsync(tenantId, ct);
        return result is null
            ? Problem(detail: $"Tenant not found: {tenantId}", statusCode: 404, title: "Not Found")
            : Ok(result);
    }

    /// <summary>
    /// Reset the sync cursor for every connector configured on a target tenant, forcing a re-pull
    /// of history. Use after fixing a connector bug to push corrected data to an affected tenant.
    /// </summary>
    /// <remarks>
    /// First cut runs synchronously and returns a per-connector <see cref="SyncResult"/>. A full
    /// re-pull can take minutes per connector, so this can block for a while on data-heavy tenants.
    /// TODO: move to a background job with progress polling (cf. the migration job model) before
    /// fanning out across many tenants at once.
    /// </remarks>
    /// <param name="tenantId">The target tenant whose connectors should be re-pulled.</param>
    /// <param name="request">Optional lower bound and data-type filter, mirroring the per-tenant reset endpoint.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{tenantId:guid}/reset-cursors")]
    [RemoteCommand(Invalidates = ["GetTenantConnectors"])]
    [ProducesResponseType(typeof(TenantCursorResetResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantCursorResetResult>> ResetTenantCursors(
        Guid tenantId,
        [FromBody] AdminResetCursorsRequest request,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Platform admin cursor reset requested for tenant {TenantId} (from {From})",
            tenantId, request.From?.ToString("o") ?? "beginning");

        var result = await _cursorResetService.ResetTenantCursorsAsync(
            tenantId, request.From, request.DataTypes, ct);

        return result is null
            ? Problem(detail: $"Tenant not found: {tenantId}", statusCode: 404, title: "Not Found")
            : Ok(result);
    }
}

/// <summary>
/// Request body for a cross-tenant cursor reset. Mirrors the per-tenant
/// <c>ResetCursorRequest</c> shape.
/// </summary>
public class AdminResetCursorsRequest
{
    /// <summary>
    /// Optional lower bound for the re-pull. When null, all available history is re-ingested.
    /// </summary>
    public DateTime? From { get; init; }

    /// <summary>
    /// Optional set of data types to reset. When null or empty, every supported data type is reset.
    /// </summary>
    public List<SyncDataType>? DataTypes { get; init; }
}
