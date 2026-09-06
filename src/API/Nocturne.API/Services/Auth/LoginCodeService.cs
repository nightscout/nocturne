using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Mints and redeems the single-use codes that let a platform administrator hand a browser a
/// tenant session without a second credential ceremony.
/// </summary>
/// <remarks>
/// Callers pass the <see cref="NocturneDbContext"/> the operation runs on: issuance pins one to
/// the target tenant, redemption uses the request-scoped context the host resolved. The context's
/// tenant is therefore what binds a code to a tenant on both sides, and a code minted on one
/// tenant is invisible on another's host.
/// </remarks>
public interface ILoginCodeService
{
    /// <summary>
    /// Mints a code for <paramref name="subjectId"/>. The plaintext is returned once and cannot
    /// be retrieved again. Consumed and expired codes on the same tenant are dropped here, which
    /// is the only thing that prunes the table.
    /// </summary>
    Task<LoginCode> IssueAsync(
        NocturneDbContext dbContext,
        Guid subjectId,
        string? ipAddress,
        string? userAgent,
        AuthAuditActor actor,
        CancellationToken ct = default);

    /// <summary>
    /// Redeems <paramref name="code"/> and returns the member it was minted for, or
    /// <see langword="null"/> when it is unknown, expired, already consumed, or belongs to
    /// another tenant. Redemption is a single atomic claim, so two concurrent callers cannot
    /// both succeed.
    /// </summary>
    Task<Guid?> RedeemAsync(
        NocturneDbContext dbContext,
        string code,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default);
}

/// <summary>A minted login code and the moment it stops being redeemable.</summary>
public sealed record LoginCode(string Code, DateTime ExpiresAt);

/// <inheritdoc cref="ILoginCodeService"/>
public class LoginCodeService : ILoginCodeService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly IJwtService _jwtService;
    private readonly IAuthAuditService _auditService;

    public LoginCodeService(IJwtService jwtService, IAuthAuditService auditService)
    {
        _jwtService = jwtService;
        _auditService = auditService;
    }

    /// <inheritdoc />
    public async Task<LoginCode> IssueAsync(
        NocturneDbContext dbContext,
        Guid subjectId,
        string? ipAddress,
        string? userAgent,
        AuthAuditActor actor,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        await dbContext.LoginCodes
            .Where(c => c.ConsumedAt != null || c.ExpiresAt <= now)
            .ExecuteDeleteAsync(ct);

        var code = _jwtService.GenerateRefreshToken();
        var entity = new LoginCodeEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CodeHash = HashUtils.Sha256Hex(code),
            ExpiresAt = now.Add(Lifetime),
            CreatedAt = now,
        };

        dbContext.LoginCodes.Add(entity);
        await dbContext.SaveChangesAsync(ct);

        await _auditService.LogAsync(
            AuthAuditEventType.LoginCodeIssued, subjectId, success: true,
            ipAddress: ipAddress,
            userAgent: userAgent,
            actor: actor,
            tenantId: dbContext.TenantIdOrNull);

        return new LoginCode(code, entity.ExpiresAt);
    }

    /// <inheritdoc />
    public async Task<Guid?> RedeemAsync(
        NocturneDbContext dbContext,
        string code,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        var codeHash = HashUtils.Sha256Hex(code);
        var now = DateTime.UtcNow;

        // Claiming and checking in one statement is what makes the code single-use: consumption
        // state is not a concurrency token, so a read-then-write would let two simultaneous
        // exchanges both see consumed_at IS NULL and both mint a session.
        var claimed = await dbContext.LoginCodes
            .Where(c => c.CodeHash == codeHash && c.ConsumedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);

        var subjectId = claimed == 0
            ? null
            : await dbContext.LoginCodes.AsNoTracking()
                .Where(c => c.CodeHash == codeHash)
                .Select(c => (Guid?)c.SubjectId)
                .FirstOrDefaultAsync(ct);

        await _auditService.LogAsync(
            subjectId is null ? AuthAuditEventType.LoginHandoffFailed : AuthAuditEventType.LoginHandoff,
            subjectId, success: subjectId is not null,
            ipAddress: ipAddress,
            userAgent: userAgent,
            detailsJson: JsonSerializer.Serialize(new { method = "login_code" }),
            tenantId: dbContext.TenantIdOrNull);

        return subjectId;
    }
}
