using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Middleware.Handlers;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Creates, lists, and revokes direct grant tokens (<c>noc_</c> opaque bearer tokens).
/// Shared by the self-service <see cref="DirectGrantController"/> and the platform-admin
/// tenant grant endpoints so token generation and hashing live in one place.
/// </summary>
/// <remarks>
/// Callers pass the <see cref="NocturneDbContext"/> the operation should run on: the
/// self-service endpoints use the request-scoped context, while platform-admin endpoints pin a
/// context to the target tenant. Grant rows are tenant-scoped, so the context's tenant decides
/// which grants are visible and which tenant a new grant is bound to.
/// </remarks>
/// <seealso cref="DirectGrantTokenHandler"/>
public interface IDirectGrantService
{
    /// <summary>
    /// Creates a new direct grant for <paramref name="subjectId"/>. The plaintext token is
    /// returned once and cannot be retrieved again.
    /// </summary>
    /// <param name="dbContext">The tenant-scoped context to create the grant on.</param>
    /// <param name="subjectId">The subject the grant is issued to.</param>
    /// <param name="label">The human-readable label.</param>
    /// <param name="scopes">The requested scopes; normalized before storage.</param>
    /// <param name="expiresAt">
    /// When the grant stops authenticating; null issues an open-ended grant. Rejected when it is
    /// not in the future — see <see cref="Validators.Auth.CreateDirectGrantRequestValidator"/>.
    /// </param>
    /// <param name="ipAddress">The caller's IP address, for the audit trail.</param>
    /// <param name="userAgent">The caller's user agent, for the audit trail.</param>
    /// <param name="actor">
    /// Who performed the action when it was not the grant's own subject. Leave null on the
    /// self-service path, where the subject acts for themselves; supplying an actor also files the
    /// event under <see cref="AuthAuditEventType.PlatformAdminGrantIssued"/>.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created grant, or an error message describing the invalid input.</returns>
    Task<DirectGrantCreationResult> CreateAsync(
        NocturneDbContext dbContext,
        Guid subjectId,
        string label,
        IReadOnlyCollection<string>? scopes,
        DateTime? expiresAt,
        string? ipAddress,
        string? userAgent,
        AuthAuditActor? actor = null,
        CancellationToken ct = default);

    /// <summary>
    /// Lists non-revoked direct grants, newest first. Never returns token material.
    /// </summary>
    /// <param name="dbContext">The tenant-scoped context to query.</param>
    /// <param name="subjectId">
    /// When set, only grants issued to this subject; when null, every grant on the context's
    /// tenant. Passed explicitly so a caller cannot widen the read to the whole tenant by
    /// omitting it.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    Task<List<DirectGrantDto>> ListAsync(
        NocturneDbContext dbContext, Guid? subjectId, CancellationToken ct = default);

    /// <summary>
    /// Revokes a direct grant by setting its <c>RevokedAt</c> timestamp. Idempotent.
    /// </summary>
    /// <param name="dbContext">The tenant-scoped context to revoke on.</param>
    /// <param name="grantId">The grant to revoke.</param>
    /// <param name="subjectId">When set, the grant must belong to this subject.</param>
    /// <param name="ipAddress">The caller's IP address, for the audit trail.</param>
    /// <param name="userAgent">The caller's user agent, for the audit trail.</param>
    /// <param name="actor">
    /// Who performed the action when it was not the grant's own subject; see
    /// <see cref="CreateAsync"/>.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns><c>true</c> when the grant was found (revoked now or already); <c>false</c> otherwise.</returns>
    Task<bool> RevokeAsync(
        NocturneDbContext dbContext,
        Guid grantId,
        Guid? subjectId,
        string? ipAddress,
        string? userAgent,
        AuthAuditActor? actor = null,
        CancellationToken ct = default);
}

/// <summary>
/// The outcome of a direct grant creation: either the created grant with its single-use
/// plaintext token, or an error message describing the invalid input.
/// </summary>
public sealed record DirectGrantCreationResult(CreateDirectGrantResponse? Response, string? Error)
{
    public static DirectGrantCreationResult Created(CreateDirectGrantResponse response) => new(response, null);
    public static DirectGrantCreationResult Invalid(string error) => new(null, error);
}

/// <inheritdoc cref="IDirectGrantService"/>
public class DirectGrantService : IDirectGrantService
{
    private const string TokenPrefix = "noc_";
    private const int TokenRandomBytes = 32;

    private readonly IAuthAuditService _auditService;
    private readonly ILogger<DirectGrantService> _logger;

    public DirectGrantService(IAuthAuditService auditService, ILogger<DirectGrantService> logger)
    {
        _auditService = auditService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DirectGrantCreationResult> CreateAsync(
        NocturneDbContext dbContext,
        Guid subjectId,
        string label,
        IReadOnlyCollection<string>? scopes,
        DateTime? expiresAt,
        string? ipAddress,
        string? userAgent,
        AuthAuditActor? actor = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return DirectGrantCreationResult.Invalid("Label is required");
        }

        if (scopes == null || scopes.Count == 0)
        {
            return DirectGrantCreationResult.Invalid("At least one scope is required");
        }

        var normalizedScopes = Scope.Normalize(scopes).ToList();
        if (normalizedScopes.Count == 0)
        {
            return DirectGrantCreationResult.Invalid("No valid scopes provided");
        }

        var randomBytes = RandomNumberGenerator.GetBytes(TokenRandomBytes);
        var plaintextToken = TokenPrefix + Base64UrlEncode(randomBytes);
        var tokenHash = DirectGrantTokenHandler.ComputeSha256Hex(plaintextToken);

        var entity = new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            ClientEntityId = null,
            SubjectId = subjectId,
            GrantType = OAuthGrantTypes.Direct,
            Scopes = normalizedScopes,
            Label = label,
            TokenHash = tokenHash,
            // Also store the SHA-1 of the token so uploaders that use the legacy Nightscout
            // api-secret protocol (Loop, AAPS, Trio, iAPS) — which pre-hash the value with SHA-1
            // before sending — authenticate with this same token via ApiKeyHandler's legacy path.
            LegacySecretHash = HashUtils.Sha1Hex(plaintextToken),
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow,
        };

        dbContext.OAuthGrants.Add(entity);
        await dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "DirectGrantAudit: {Event} grant_id={GrantId} subject_id={SubjectId} scopes={Scopes}",
            "direct_grant_created", entity.Id, subjectId, string.Join(" ", normalizedScopes));

        await _auditService.LogAsync(
            actor is null ? AuthAuditEventType.TokenIssued : AuthAuditEventType.PlatformAdminGrantIssued,
            subjectId, success: true,
            ipAddress: ipAddress,
            userAgent: userAgent,
            detailsJson: JsonSerializer.Serialize(new { method = "direct_grant", grant_id = entity.Id }),
            actor: actor,
            tenantId: dbContext.TenantIdOrNull);

        return DirectGrantCreationResult.Created(new CreateDirectGrantResponse
        {
            Id = entity.Id,
            Token = plaintextToken,
            Label = entity.Label!,
            Scopes = normalizedScopes,
            CreatedAt = entity.CreatedAt,
            ExpiresAt = entity.ExpiresAt,
        });
    }

    /// <inheritdoc />
    public async Task<List<DirectGrantDto>> ListAsync(
        NocturneDbContext dbContext, Guid? subjectId, CancellationToken ct = default)
    {
        return await dbContext.OAuthGrants
            .AsNoTracking()
            .Where(g => (subjectId == null || g.SubjectId == subjectId.Value)
                     && g.GrantType == OAuthGrantTypes.Direct
                     && g.RevokedAt == null)
            .OrderByDescending(g => g.CreatedAt)
            .Select(g => new DirectGrantDto
            {
                Id = g.Id,
                SubjectId = g.SubjectId,
                Label = g.Label ?? string.Empty,
                Scopes = g.Scopes,
                CreatedAt = g.CreatedAt,
                ExpiresAt = g.ExpiresAt,
                LastUsedAt = g.LastUsedAt,
                IsLegacy = g.IsMigrated,
            })
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(
        NocturneDbContext dbContext,
        Guid grantId,
        Guid? subjectId,
        string? ipAddress,
        string? userAgent,
        AuthAuditActor? actor = null,
        CancellationToken ct = default)
    {
        var grant = await dbContext.OAuthGrants
            .Where(g => g.Id == grantId
                     && (subjectId == null || g.SubjectId == subjectId.Value)
                     && g.GrantType == OAuthGrantTypes.Direct)
            .FirstOrDefaultAsync(ct);

        if (grant == null)
        {
            return false;
        }

        if (grant.RevokedAt.HasValue)
        {
            return true;
        }

        grant.RevokedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "DirectGrantAudit: {Event} grant_id={GrantId} subject_id={SubjectId}",
            "direct_grant_revoked", grantId, grant.SubjectId);

        await _auditService.LogAsync(
            actor is null ? AuthAuditEventType.TokenRevoked : AuthAuditEventType.PlatformAdminGrantRevoked,
            grant.SubjectId, success: true,
            ipAddress: ipAddress,
            userAgent: userAgent,
            detailsJson: JsonSerializer.Serialize(new { grant_id = grantId }),
            actor: actor,
            tenantId: dbContext.TenantIdOrNull);

        return true;
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
