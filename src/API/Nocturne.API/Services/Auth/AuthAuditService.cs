using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Writes authentication and authorization audit events to the <c>auth_audit_log</c> table.
/// </summary>
/// <remarks>
/// Each call to <see cref="LogAsync"/> commits its single row on a context of its own. Audit logging
/// is non-blocking by design: any database exception is swallowed and logged as a warning so that a
/// transient storage failure never prevents an authentication response from reaching the caller.
/// </remarks>
/// <seealso cref="IAuthAuditService"/>
public class AuthAuditService : IAuthAuditService
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<AuthAuditService> _logger;

    /// <summary>
    /// Creates a new instance of AuthAuditService.
    /// </summary>
    public AuthAuditService(
        INocturneDbContextFactory contexts,
        IHttpContextAccessor httpContextAccessor,
        IAuditContext auditContext,
        ILogger<AuthAuditService> logger)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _httpContextAccessor = httpContextAccessor;
        _auditContext = auditContext;
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

        try
        {
            await using var db = await _contexts.CreateAsync();
            db.AuthAuditLog.Add(new AuthAuditLogEntity
            {
                Id = Guid.CreateVersion7(),
                EventType = eventType,
                SubjectId = subjectId,
                // No actor to distinguish means the subject acted for themselves.
                ActorSubjectId = actor is null ? subjectId : actor.SubjectId,
                ActorCredential = actor?.Credential,
                TenantId = tenantId ?? db.TenantIdOrNull,
                Success = success,
                IpAddress = ipAddress,
                UserAgent = userAgent,
                ErrorMessage = errorMessage,
                DetailsJson = detailsJson,
                RefreshTokenId = refreshTokenId,
                TraceId = _auditContext.TraceId,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Audit logging must never block the main operation
            _logger.LogWarning(ex, "Failed to write auth audit log entry ({EventType})", eventType);
        }
    }
}
