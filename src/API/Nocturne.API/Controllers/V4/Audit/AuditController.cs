using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Responses;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.API.Extensions;

namespace Nocturne.API.Controllers.V4.Audit;

/// <summary>
/// Endpoints for querying mutation and read audit logs and managing audit configuration.
/// </summary>
[ApiController]
[Tags("Platform")]
[Route("api/v4/audit")]
[Produces("application/json")]
[Authorize]
public class AuditController : ControllerBase
{
    private const string OldestFirst = "created_at_asc";

    private readonly IAuditLogReader _auditLog;
    private readonly IAuditConfigStore _auditConfig;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ITenantAuditConfigCache _configCache;
    private readonly IConfiguration _configuration;

    public AuditController(
        IAuditLogReader auditLog,
        IAuditConfigStore auditConfig,
        ITenantAccessor tenantAccessor,
        ITenantAuditConfigCache configCache,
        IConfiguration configuration)
    {
        _auditLog = auditLog;
        _auditConfig = auditConfig;
        _tenantAccessor = tenantAccessor;
        _configCache = configCache;
        _configuration = configuration;
    }

    /// <summary>
    /// Query mutation audit log entries for the current tenant.
    /// </summary>
    [HttpGet("mutations")]
    [RemoteQuery]
    [ProducesResponseType(typeof(PaginatedResponse<MutationAuditDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetMutationAuditLog(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "created_at_desc",
        [FromQuery] Guid? actorSubjectId = null,
        [FromQuery] string? entityType = null,
        [FromQuery] string? action = null,
        [FromQuery] Guid? entityId = null,
        CancellationToken ct = default)
    {
        if (!HttpContext.HasScope(Scope.AuditRead))
            return Forbid();

        limit = V4ReadLimits.ClampLimit(limit);
        offset = V4ReadLimits.ClampOffset(offset);

        var page = await _auditLog.GetMutationsAsync(
            new AuditLogWindow(from, to, limit, offset, sort == OldestFirst, actorSubjectId, entityType),
            new MutationAuditFilter(action, entityId),
            ct);

        return Ok(new PaginatedResponse<MutationAuditDto>
        {
            Data = page.Data.Select(e => new MutationAuditDto
            {
                Id = e.Id,
                CreatedAt = e.CreatedAt,
                EntityType = e.EntityType,
                EntityId = e.EntityId,
                Action = e.Action,
                Changes = e.Changes,
                ActorSubjectId = e.ActorSubjectId,
                ActorName = e.ActorName,
                AuthType = e.AuthType,
                IpAddress = e.IpAddress,
                Endpoint = e.Endpoint,
            }).ToList(),
            Pagination = page.Pagination,
        });
    }

    /// <summary>
    /// Query read access audit log entries for the current tenant.
    /// </summary>
    [HttpGet("reads")]
    [RemoteQuery]
    [ProducesResponseType(typeof(PaginatedResponse<ReadAccessAuditDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetReadAccessAuditLog(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "created_at_desc",
        [FromQuery] Guid? actorSubjectId = null,
        [FromQuery] string? entityType = null,
        [FromQuery] string? endpoint = null,
        [FromQuery] int? statusCode = null,
        CancellationToken ct = default)
    {
        if (!HttpContext.HasScope(Scope.AuditRead))
            return Forbid();

        limit = V4ReadLimits.ClampLimit(limit);
        offset = V4ReadLimits.ClampOffset(offset);

        var page = await _auditLog.GetReadsAsync(
            new AuditLogWindow(from, to, limit, offset, sort == OldestFirst, actorSubjectId, entityType),
            new ReadAccessLogFilter(endpoint, statusCode),
            ct);

        return Ok(new PaginatedResponse<ReadAccessAuditDto>
        {
            Data = page.Data.Select(e => new ReadAccessAuditDto
            {
                Id = e.Id,
                CreatedAt = e.CreatedAt,
                Endpoint = e.Endpoint,
                EntityType = e.EntityType,
                RecordCount = e.RecordCount,
                StatusCode = e.StatusCode,
                QueryParameters = e.QueryParameters,
                ActorSubjectId = e.ActorSubjectId,
                ActorName = e.ActorName,
                AuthType = e.AuthType,
                IpAddress = e.IpAddress,
                CredentialFingerprint = e.CredentialFingerprint,
            }).ToList(),
            Pagination = page.Pagination,
        });
    }

    /// <summary>
    /// Get the audit configuration for the current tenant.
    /// </summary>
    [HttpGet("config")]
    [RemoteQuery]
    [ProducesResponseType(typeof(AuditConfigDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAuditConfig(CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.AuditRead))
            return Forbid();

        var config = await _configCache.GetConfigAsync(_tenantAccessor.TenantId, ct);

        return Ok(new AuditConfigDto
        {
            ReadAuditEnabled = config.ReadAuditEnabled,
            ReadAuditRetentionDays = config.ReadAuditRetentionDays,
            MutationAuditRetentionDays = config.MutationAuditRetentionDays,
        });
    }

    /// <summary>
    /// Create or update the audit configuration for the current tenant.
    /// </summary>
    [HttpPut("config")]
    [RemoteCommand(Invalidates = ["GetAuditConfig"])]
    [ProducesResponseType(typeof(AuditConfigDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateAuditConfig(
        [FromBody] AuditConfigDto request,
        CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.AuditManage))
            return Forbid();

        var tenantId = _tenantAccessor.TenantId;

        // Mutation audit must outlive any soft-deleted entity it describes, so a user-delete's
        // attribution is still on record for as long as the entity itself is recoverable.
        // Compare against the effective windows both sweeps actually apply — neither side has a
        // "kept indefinitely" state, so a null on either config row means that side's platform
        // default, not infinity. The symmetric check (rejecting a soft-delete bump above audit
        // retention) belongs on the TenantDataRetentionConfig update endpoint when it exists.
        var softDeleteDays = await _auditConfig.GetSoftDeleteRetentionDaysAsync(ct);

        var effectiveSoftDeleteDays = SoftDeleteRetentionPolicy.ResolveDays(
            softDeleteDays, _configuration);

        if (request.MutationAuditRetentionDays is int requested && requested < effectiveSoftDeleteDays)
        {
            return BadRequest(new
            {
                error = $"Mutation audit retention ({requested} days) must be >= the effective "
                      + $"soft-delete retention ({effectiveSoftDeleteDays} days). Audit rows "
                      + "must outlive the soft-deleted entities they describe, otherwise "
                      + "user-delete attribution is lost while the entity is still recoverable.",
            });
        }

        // A null defers to the platform default, which an instance may configure below this
        // tenant's soft-delete window. Store the floor rather than refusing: the tenant did not
        // choose the failing value, and rejecting would leave them unable to change any other
        // audit setting, including turning read auditing off.
        var mutationDays = request.MutationAuditRetentionDays;
        if (mutationDays is null
            && AuditRetentionPolicy.ResolveMutationDays(null, _configuration) is int platformDays
            && platformDays < effectiveSoftDeleteDays)
        {
            mutationDays = effectiveSoftDeleteDays;
        }

        var saved = await _auditConfig.SaveAsync(
            new TenantAuditConfig(request.ReadAuditEnabled, request.ReadAuditRetentionDays, mutationDays),
            ct);

        _configCache.Invalidate(tenantId);

        return Ok(new AuditConfigDto
        {
            ReadAuditEnabled = saved.ReadAuditEnabled,
            ReadAuditRetentionDays = saved.ReadAuditRetentionDays,
            MutationAuditRetentionDays = saved.MutationAuditRetentionDays,
        });
    }

}
