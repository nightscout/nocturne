using Nocturne.API.Extensions;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Writes authentication and authorization audit events to the <c>auth_audit_log</c> table.
/// </summary>
/// <remarks>
/// <para>
/// Each call to <see cref="LogAsync"/> opens its own <c>SaveChangesAsync</c> to commit the
/// single row immediately; it does not participate in any ambient unit-of-work.
/// </para>
/// <para>
/// Audit logging is non-blocking by design: any database exception is swallowed and logged as a
/// warning so that a transient storage failure never prevents an authentication response from
/// reaching the caller.
/// </para>
/// </remarks>
/// <seealso cref="IAuthAuditService"/>
public class AuthAuditService : IAuthAuditService
{
    private readonly NocturneDbContext _dbContext;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuthAuditService> _logger;

    /// <summary>
    /// Creates a new instance of AuthAuditService.
    /// </summary>
    public AuthAuditService(
        NocturneDbContext dbContext,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AuthAuditService> logger)
    {
        _dbContext = dbContext;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task LogAsync(string eventType, Guid? subjectId, bool success,
        string? ipAddress = null, string? userAgent = null,
        string? errorMessage = null, string? detailsJson = null,
        Guid? refreshTokenId = null,
        AuthAuditActor? actor = null,
        Guid? tenantId = null)
    {
        actor ??= AuthAuditActor.FromCallerOtherThan(
            _httpContextAccessor.HttpContext?.GetAuthContext(), subjectId);
        tenantId ??= _dbContext.TenantIdOrNull;

        try
        {
            _dbContext.AuthAuditLog.Add(new AuthAuditLogEntity
            {
                Id = Guid.CreateVersion7(),
                EventType = eventType,
                SubjectId = subjectId,
                // No actor to distinguish means the subject acted for themselves.
                ActorSubjectId = actor is null ? subjectId : actor.SubjectId,
                ActorCredential = actor?.Credential,
                TenantId = tenantId,
                Success = success,
                IpAddress = ipAddress,
                UserAgent = userAgent,
                ErrorMessage = errorMessage,
                DetailsJson = detailsJson,
                RefreshTokenId = refreshTokenId,
                CreatedAt = DateTime.UtcNow,
            });
            await _dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Audit logging must never block the main operation
            _logger.LogWarning(ex, "Failed to write auth audit log entry ({EventType})", eventType);
        }
    }
}
