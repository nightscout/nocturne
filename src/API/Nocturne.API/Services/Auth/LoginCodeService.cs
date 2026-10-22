using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Mints and redeems single-use codes bound to one member of one tenant: a login code hands a
/// browser a tenant session without a second credential ceremony, and a recovery code lets a member
/// with no sign-in method left enrol a passkey (<see cref="LoginCodePurpose"/>).
/// </summary>
/// <remarks>
/// Callers pass the <see cref="NocturneDbContext"/> the operation runs on: issuance pins one to
/// the target tenant, redemption uses one from the request's factory, which reads the tenant the
/// host resolved. The context's tenant is therefore what binds a code to a tenant on both sides,
/// and a code minted on one tenant is invisible on another's host.
/// </remarks>
public interface ILoginCodeService
{
    /// <summary>
    /// Mints a code for <paramref name="subjectId"/>. The plaintext is returned once and cannot
    /// be retrieved again. Consumed and expired codes on the same tenant are dropped here, which
    /// is the only thing that prunes the table. A recovery code replaces any the member already
    /// had outstanding (<see cref="LoginCodeService.VoidRecoveryCodesAsync"/>).
    /// </summary>
    Task<LoginCode> IssueAsync(
        NocturneDbContext dbContext,
        Guid subjectId,
        LoginCodePurpose purpose,
        string? ipAddress,
        string? userAgent,
        AuthAuditActor actor,
        CancellationToken ct = default);

    /// <summary>
    /// Redeems <paramref name="code"/> and returns the member and purpose it was minted for, or
    /// <see langword="null"/> when it is unknown, expired, already consumed, minted for a purpose
    /// outside <paramref name="purposes"/>, or belongs to another tenant. Redemption is a single
    /// atomic claim, so two concurrent callers cannot both succeed.
    /// </summary>
    Task<RedeemedLoginCode?> RedeemAsync(
        NocturneDbContext dbContext,
        string code,
        IReadOnlyCollection<LoginCodePurpose> purposes,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default);
}

/// <summary>A minted login code and the moment it stops being redeemable.</summary>
public sealed record LoginCode(string Code, DateTime ExpiresAt);

/// <summary>Whom a redeemed code was minted for, and for what.</summary>
public sealed record RedeemedLoginCode(Guid SubjectId, LoginCodePurpose Purpose);

/// <inheritdoc cref="ILoginCodeService"/>
public class LoginCodeService : ILoginCodeService
{
    /// <summary>
    /// How long a code stays redeemable. A login code is handed straight to a browser; a recovery
    /// code is delivered to the member out of band, so it has to survive the delivery.
    /// </summary>
    public static TimeSpan LifetimeOf(LoginCodePurpose purpose) =>
        RecoveryPurposes.Contains(purpose) ? TimeSpan.FromHours(24) : TimeSpan.FromMinutes(5);

    /// <summary>The purposes that redeem at <c>POST /api/auth/passkey/recovery-mode/redeem</c>.</summary>
    public static readonly IReadOnlyList<LoginCodePurpose> RecoveryPurposes =
        [LoginCodePurpose.Recovery, LoginCodePurpose.OperatorRecovery];

    /// <summary>
    /// Spends every outstanding recovery code for <paramref name="subjectId"/> on the context's
    /// tenant. A recovery code is a bearer link that outlives the situation it was issued for, so
    /// it stops working once the member enrols a passkey or a newer code replaces it.
    /// </summary>
    public static Task VoidRecoveryCodesAsync(NocturneDbContext dbContext, Guid subjectId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return dbContext.LoginCodes
            .Where(c => c.SubjectId == subjectId && c.ConsumedAt == null && RecoveryPurposes.Contains(c.Purpose))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);
    }

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
        LoginCodePurpose purpose,
        string? ipAddress,
        string? userAgent,
        AuthAuditActor actor,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        await dbContext.LoginCodes
            .Where(c => c.ConsumedAt != null || c.ExpiresAt <= now)
            .ExecuteDeleteAsync(ct);

        if (RecoveryPurposes.Contains(purpose))
            await VoidRecoveryCodesAsync(dbContext, subjectId, ct);

        var code = _jwtService.GenerateRefreshToken();
        var entity = new LoginCodeEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CodeHash = HashUtils.Sha256Hex(code),
            Purpose = purpose,
            ExpiresAt = now.Add(LifetimeOf(purpose)),
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
    public async Task<RedeemedLoginCode?> RedeemAsync(
        NocturneDbContext dbContext,
        string code,
        IReadOnlyCollection<LoginCodePurpose> purposes,
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
            .Where(c => c.CodeHash == codeHash && purposes.Contains(c.Purpose) && c.ConsumedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);

        var redeemed = claimed == 0
            ? null
            : await dbContext.LoginCodes.AsNoTracking()
                .Where(c => c.CodeHash == codeHash)
                .Select(c => new RedeemedLoginCode(c.SubjectId, c.Purpose))
                .FirstOrDefaultAsync(ct);
        var subjectId = redeemed?.SubjectId;

        await _auditService.LogAsync(
            subjectId is null ? AuthAuditEventType.LoginHandoffFailed : AuthAuditEventType.LoginHandoff,
            subjectId, success: subjectId is not null,
            ipAddress: ipAddress,
            userAgent: userAgent,
            detailsJson: JsonSerializer.Serialize(new
            {
                method = purposes.Any(RecoveryPurposes.Contains) ? "recovery_code_link" : "login_code",
            }),
            tenantId: dbContext.TenantIdOrNull);

        return redeemed;
    }
}
