using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Models.Responses;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Controllers.V4.Audit;

/// <summary>
/// Endpoints for querying mutation and read audit logs and managing audit configuration.
/// </summary>
[ApiController]
[Route("api/v4/audit")]
[Produces("application/json")]
public class AuditController : ControllerBase
{
    private readonly IDbContextFactory<NocturneDbContext> _contextFactory;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ITenantAuditConfigCache _configCache;

    public AuditController(
        IDbContextFactory<NocturneDbContext> contextFactory,
        ITenantAccessor tenantAccessor,
        ITenantAuditConfigCache configCache)
    {
        _contextFactory = contextFactory;
        _tenantAccessor = tenantAccessor;
        _configCache = configCache;
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
        [FromQuery] Guid? subjectId = null,
        [FromQuery] string? entityType = null,
        [FromQuery] string? action = null,
        [FromQuery] Guid? entityId = null,
        CancellationToken ct = default)
    {
        if (!HasPermission(TenantPermissions.AuditRead))
            return Forbid();

        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        db.TenantId = _tenantAccessor.TenantId;

        var query = db.MutationAuditLog.AsNoTracking()
            .Where(e => e.CreatedAt >= from && e.CreatedAt <= to);

        if (subjectId.HasValue)
            query = query.Where(e => e.SubjectId == subjectId.Value);
        if (!string.IsNullOrEmpty(entityType))
            query = query.Where(e => e.EntityType == entityType);
        if (!string.IsNullOrEmpty(action))
            query = query.Where(e => e.Action == action);
        if (entityId.HasValue)
            query = query.Where(e => e.EntityId == entityId.Value);

        var total = await query.CountAsync(ct);

        query = sort switch
        {
            "created_at_asc" => query.OrderBy(e => e.CreatedAt),
            _ => query.OrderByDescending(e => e.CreatedAt),
        };

        var items = await query
            .Skip(offset)
            .Take(limit)
            .Select(e => new MutationAuditDto
            {
                Id = e.Id,
                CreatedAt = e.CreatedAt,
                EntityType = e.EntityType,
                EntityId = e.EntityId,
                Action = e.Action,
                Changes = e.ChangesJson,
                SubjectId = e.SubjectId,
                SubjectName = e.SubjectName,
                AuthType = e.AuthType,
                IpAddress = e.IpAddress,
                Endpoint = e.Endpoint,
            })
            .ToListAsync(ct);

        return Ok(new PaginatedResponse<MutationAuditDto>
        {
            Data = items,
            Pagination = new PaginationInfo(limit, offset, total),
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
        [FromQuery] Guid? subjectId = null,
        [FromQuery] string? entityType = null,
        [FromQuery] string? endpoint = null,
        [FromQuery] int? statusCode = null,
        CancellationToken ct = default)
    {
        if (!HasPermission(TenantPermissions.AuditRead))
            return Forbid();

        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        db.TenantId = _tenantAccessor.TenantId;

        var query = db.ReadAccessLog.AsNoTracking()
            .Where(e => e.CreatedAt >= from && e.CreatedAt <= to);

        if (subjectId.HasValue)
            query = query.Where(e => e.SubjectId == subjectId.Value);
        if (!string.IsNullOrEmpty(entityType))
            query = query.Where(e => e.EntityType == entityType);
        if (!string.IsNullOrEmpty(endpoint))
            query = query.Where(e => e.Endpoint == endpoint);
        if (statusCode.HasValue)
            query = query.Where(e => e.StatusCode == statusCode.Value);

        var total = await query.CountAsync(ct);

        query = sort switch
        {
            "created_at_asc" => query.OrderBy(e => e.CreatedAt),
            _ => query.OrderByDescending(e => e.CreatedAt),
        };

        var items = await query
            .Skip(offset)
            .Take(limit)
            .Select(e => new ReadAccessAuditDto
            {
                Id = e.Id,
                CreatedAt = e.CreatedAt,
                Endpoint = e.Endpoint,
                EntityType = e.EntityType,
                RecordCount = e.RecordCount,
                StatusCode = e.StatusCode,
                QueryParameters = e.QueryParametersJson,
                SubjectId = e.SubjectId,
                SubjectName = e.SubjectName,
                AuthType = e.AuthType,
                IpAddress = e.IpAddress,
                ApiSecretHashPrefix = e.ApiSecretHashPrefix,
            })
            .ToListAsync(ct);

        return Ok(new PaginatedResponse<ReadAccessAuditDto>
        {
            Data = items,
            Pagination = new PaginationInfo(limit, offset, total),
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
        if (!HasPermission(TenantPermissions.AuditRead))
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
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateAuditConfig(
        [FromBody] AuditConfigDto request,
        CancellationToken ct)
    {
        if (!HasPermission(TenantPermissions.AuditManage))
            return Forbid();

        var tenantId = _tenantAccessor.TenantId;

        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var entity = await db.TenantAuditConfig
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);

        var now = DateTime.UtcNow;

        if (entity == null)
        {
            entity = new TenantAuditConfigEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ReadAuditEnabled = request.ReadAuditEnabled,
                ReadAuditRetentionDays = request.ReadAuditRetentionDays,
                MutationAuditRetentionDays = request.MutationAuditRetentionDays,
                SysCreatedAt = now,
                SysUpdatedAt = now,
            };
            db.TenantAuditConfig.Add(entity);
        }
        else
        {
            entity.ReadAuditEnabled = request.ReadAuditEnabled;
            entity.ReadAuditRetentionDays = request.ReadAuditRetentionDays;
            entity.MutationAuditRetentionDays = request.MutationAuditRetentionDays;
            entity.SysUpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);

        _configCache.Invalidate(tenantId);

        return Ok(new AuditConfigDto
        {
            ReadAuditEnabled = entity.ReadAuditEnabled,
            ReadAuditRetentionDays = entity.ReadAuditRetentionDays,
            MutationAuditRetentionDays = entity.MutationAuditRetentionDays,
        });
    }

    private bool HasPermission(string permission)
    {
        var grantedScopes = HttpContext.Items["GrantedScopes"] as IReadOnlySet<string>;
        if (grantedScopes == null) return false;
        return TenantPermissions.HasPermission(grantedScopes, permission);
    }
}
