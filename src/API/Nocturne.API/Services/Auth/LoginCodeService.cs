using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;

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
    /// be retrieved again. Consumed and expired codes on the same tenant are dropped here and by
    /// <see cref="IssueEmailedAsync"/>, which are the only things that prune the table, except the
    /// emailed codes the throttle of <see cref="IssueEmailedAsync"/> still counts. A code of any purpose but
    /// <see cref="LoginCodePurpose.Login"/> replaces the member's unspent ones of its kind
    /// (<see cref="LoginCodeService.SupersededBy"/>). An emailed code is minted only by
    /// <see cref="IssueEmailedAsync"/>.
    /// </summary>
    /// <param name="dbContext">A context on the code's tenant.</param>
    /// <param name="subjectId">The member the code is for.</param>
    /// <param name="purpose">What the code can be exchanged for.</param>
    /// <param name="ipAddress">The caller's address, for the audit row.</param>
    /// <param name="userAgent">The caller's user agent, for the audit row.</param>
    /// <param name="actor">Who issued the code, for the audit row.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<LoginCode> IssueAsync(
        NocturneDbContext dbContext,
        Guid subjectId,
        LoginCodePurpose purpose,
        string? ipAddress,
        string? userAgent,
        AuthAuditActor actor,
        CancellationToken ct = default);

    /// <summary>
    /// Mints a code of one of <see cref="LoginCodeService.EmailPurposes"/>, bound to
    /// <paramref name="email"/>, as <see cref="IssueAsync"/> does, unless the member was sent one
    /// too recently; then it mints nothing and returns null. The throttle exists so that nobody
    /// can flood a member's inbox from many client addresses: none of that purpose in the last
    /// <see cref="LoginCodeService.EmailInterval"/>, and fewer than
    /// <see cref="LoginCodeService.EmailsPerWindow"/> in the last
    /// <see cref="LoginCodeService.EmailWindow"/>, counted per site because codes are
    /// tenant-scoped. The count and the mint hold the member's lock in one transaction, so
    /// concurrent requests cannot all pass the count.
    /// </summary>
    /// <remarks>
    /// Anyone who knows a member's confirmed address can use up the day's allowance, and each new
    /// code voids the last, so the member's own request may be dropped for up to a day. The flood
    /// the throttle prevents is worse, and the member still has their own recovery codes and an
    /// administrator.
    /// </remarks>
    Task<LoginCode?> IssueEmailedAsync(
        NocturneDbContext dbContext,
        Guid subjectId,
        LoginCodePurpose purpose,
        string email,
        string? ipAddress,
        string? userAgent,
        AuthAuditActor actor,
        CancellationToken ct = default);

    /// <summary>
    /// The member and purpose of <paramref name="code"/> when it is live and minted for one of
    /// <paramref name="purposes"/> on the context's tenant and <paramref name="admits"/> accepts it
    /// (as in <see cref="RedeemAsync"/>), without spending it; otherwise null.
    /// </summary>
    Task<RedeemedLoginCode?> FindLiveAsync(
        NocturneDbContext dbContext,
        string code,
        IReadOnlyCollection<LoginCodePurpose> purposes,
        Func<RedeemedLoginCode, CancellationToken, Task<bool>>? admits = null,
        CancellationToken ct = default);

    /// <summary>
    /// Redeems <paramref name="code"/> and returns the member and purpose it was minted for, or
    /// <see langword="null"/> when it is unknown, expired, already consumed, minted for a purpose
    /// outside <paramref name="purposes"/>, belongs to another tenant, is a login code or recovery
    /// session whose member is no longer active (<see cref="LoginCodeService.UsableByItsSubject"/>), or is
    /// refused by <paramref name="admits"/>. A code is spent only by a
    /// redemption that returns it: the claim is conditional on the code being unspent, so two
    /// concurrent callers cannot both succeed.
    /// </summary>
    /// <param name="dbContext">The context the redemption runs on, in one transaction.</param>
    /// <param name="code">The code as presented.</param>
    /// <param name="purposes">The purposes the caller redeems.</param>
    /// <param name="admits">
    /// Whether the code's member may redeem it now. It runs before the claim, so a refusal leaves
    /// the code unspent.
    /// </param>
    /// <param name="withSpend">
    /// Work that runs after the claim, inside its transaction: when it throws, the claim rolls back and
    /// the code is not spent. A write it makes through another context commits on its own. A transient
    /// failure runs it again, except after a commit that reported failure but landed.
    /// </param>
    /// <param name="ipAddress">The caller's address, for the audit row.</param>
    /// <param name="userAgent">The caller's user agent, for the audit row.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<RedeemedLoginCode?> RedeemAsync(
        NocturneDbContext dbContext,
        string code,
        IReadOnlyCollection<LoginCodePurpose> purposes,
        Func<RedeemedLoginCode, CancellationToken, Task<bool>>? admits,
        Func<RedeemedLoginCode, CancellationToken, Task>? withSpend,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default);

    /// <summary>
    /// Spends every outstanding code of <see cref="LoginCodeService.SessionStartingPurposes"/> for
    /// <paramref name="subjectId"/> on the context's tenant. Such a code is a bearer link that
    /// outlives the situation it was issued for, so it stops working once the member enrols a way
    /// to sign in.
    /// </summary>
    Task VoidRecoveryCodesAsync(NocturneDbContext dbContext, Guid subjectId, CancellationToken ct = default);
}

/// <summary>A minted login code and the moment it stops being redeemable.</summary>
public sealed record LoginCode(string Code, DateTime ExpiresAt);

/// <summary>
/// Whom a redeemed code was minted for, for what, and for an emailed code the address it was sent to.
/// </summary>
public sealed record RedeemedLoginCode(Guid SubjectId, LoginCodePurpose Purpose, string? Email = null)
{
    /// <summary>The code's row, which its issue and redeem audit rows both name.</summary>
    public Guid CodeId { get; init; }
}

/// <inheritdoc cref="ILoginCodeService"/>
public class LoginCodeService : ILoginCodeService
{
    /// <summary>
    /// How long a code stays redeemable. A login code is handed straight to a browser. A code an
    /// administrator or the operator issues is handed to the member by hand, and a confirmation
    /// link waits in an inbox, so both have to survive the delivery. An emailed recovery link
    /// stands in for a sign-in, so it lives only as long as reading the message takes.
    /// </summary>
    public static TimeSpan LifetimeOf(LoginCodePurpose purpose) => purpose switch
    {
        LoginCodePurpose.Recovery or LoginCodePurpose.OperatorRecovery or LoginCodePurpose.EmailConfirm
            => TimeSpan.FromHours(24),
        LoginCodePurpose.EmailRecovery => TimeSpan.FromMinutes(30),
        LoginCodePurpose.RecoverySession => TimeSpan.FromMinutes(10),
        _ => TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// The codes someone with authority over the member issues, which redeem only while
    /// <see cref="MemberRecoveryEligibility"/> allows it.
    /// </summary>
    public static readonly IReadOnlyList<LoginCodePurpose> RecoveryPurposes =
        [LoginCodePurpose.Recovery, LoginCodePurpose.OperatorRecovery];

    /// <summary>
    /// The purposes that redeem at <c>POST /api/auth/passkey/recovery-mode/redeem</c> for a
    /// <see cref="LoginCodePurpose.RecoverySession"/>. The member's emailed link is one, but
    /// <see cref="MemberRecoveryEligibility"/> does not admit it, hence a list apart from
    /// <see cref="RecoveryPurposes"/>.
    /// </summary>
    public static readonly IReadOnlyList<LoginCodePurpose> SessionStartingPurposes =
        [.. RecoveryPurposes, LoginCodePurpose.EmailRecovery];

    /// <summary>The purposes whose code is emailed, and so bound to an address.</summary>
    public static readonly IReadOnlyList<LoginCodePurpose> EmailPurposes =
        [LoginCodePurpose.EmailRecovery, LoginCodePurpose.EmailConfirm];

    /// <summary>
    /// The unspent codes a new code of <paramref name="purpose"/> voids for the same member. An
    /// administrator's and the operator's codes replace each other. An emailed code and a recovery
    /// session replace only their own kind, so neither cancels a code someone else issued.
    /// </summary>
    public static IReadOnlyList<LoginCodePurpose> SupersededBy(LoginCodePurpose purpose) =>
        purpose == LoginCodePurpose.Login ? []
        : RecoveryPurposes.Contains(purpose) ? RecoveryPurposes
        : [purpose];

    /// <summary>The shortest gap between two emailed codes of one purpose to one member.</summary>
    public static readonly TimeSpan EmailInterval = TimeSpan.FromMinutes(2);

    /// <summary>The most emailed codes of one purpose a member is sent within <see cref="EmailWindow"/>.</summary>
    public const int EmailsPerWindow = 5;

    /// <summary>The window <see cref="EmailsPerWindow"/> counts over.</summary>
    public static readonly TimeSpan EmailWindow = TimeSpan.FromDays(1);

    /// <summary>Serialises <see cref="IssueEmailedAsync"/> per member.</summary>
    private const int EmailLockClass = 0x4E45_4D4C;

    /// <summary>
    /// The throttle of <see cref="IssueEmailedAsync"/>, counted from the stored codes, spent or not,
    /// which <see cref="PruneAsync"/> keeps for the window.
    /// </summary>
    private async Task<bool> MayEmailAsync(
        NocturneDbContext dbContext, Guid subjectId, LoginCodePurpose purpose, CancellationToken ct)
    {
        var now = Now;
        var since = now - EmailWindow;
        var issued = await dbContext.LoginCodes.AsNoTracking()
            .Where(c => c.SubjectId == subjectId && c.Purpose == purpose && c.CreatedAt > since)
            .Select(c => c.CreatedAt)
            .ToListAsync(ct);
        return issued.Count < EmailsPerWindow && !issued.Any(at => at > now - EmailInterval);
    }

    /// <inheritdoc />
    public Task VoidRecoveryCodesAsync(NocturneDbContext dbContext, Guid subjectId, CancellationToken ct = default) =>
        VoidAsync(dbContext, subjectId, SessionStartingPurposes, ct);

    private Task VoidAsync(
        NocturneDbContext dbContext, Guid subjectId, IReadOnlyList<LoginCodePurpose> purposes, CancellationToken ct)
    {
        var now = Now;
        return dbContext.LoginCodes
            .Where(c => c.SubjectId == subjectId && c.ConsumedAt == null && purposes.Contains(c.Purpose))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);
    }

    private readonly IJwtService _jwtService;
    private readonly IAuthAuditService _auditService;
    private readonly TimeProvider _time;

    public LoginCodeService(IJwtService jwtService, IAuthAuditService auditService, TimeProvider time)
    {
        _jwtService = jwtService;
        _auditService = auditService;
        _time = time;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

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
        if (EmailPurposes.Contains(purpose))
            throw new ArgumentException("An emailed code is issued by IssueEmailedAsync.", nameof(purpose));

        await PruneAsync(dbContext, ct);
        var issued = await MintAsync(dbContext, subjectId, purpose, email: null, ct);
        await AuditIssueAsync(dbContext, subjectId, purpose, issued.Id, ipAddress, userAgent, actor);
        return issued.Code;
    }

    /// <inheritdoc />
    public async Task<LoginCode?> IssueEmailedAsync(
        NocturneDbContext dbContext,
        Guid subjectId,
        LoginCodePurpose purpose,
        string email,
        string? ipAddress,
        string? userAgent,
        AuthAuditActor actor,
        CancellationToken ct = default)
    {
        if (!EmailPurposes.Contains(purpose))
            throw new ArgumentException("Only an emailed code is bound to an address.", nameof(purpose));
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("An emailed code is bound to the address it is sent to.", nameof(email));

        await PruneAsync(dbContext, ct);
        var issued = await dbContext.ExecuteInTransactionAsync<MintedCode?>(async token =>
            {
                await dbContext.LockForTransactionAsync(EmailLockClass, subjectId, token);
                return await MayEmailAsync(dbContext, subjectId, purpose, token)
                    ? await MintAsync(dbContext, subjectId, purpose, email, token)
                    : null;
            },
            verifySucceeded: async (minted, token) =>
                minted is not null && await dbContext.LoginCodes.AnyAsync(c => c.Id == minted.Id, token),
            ct: ct);
        if (issued is null)
            return null;

        await AuditIssueAsync(dbContext, subjectId, purpose, issued.Id, ipAddress, userAgent, actor);
        return issued.Code;
    }

    private sealed record MintedCode(Guid Id, LoginCode Code);

    /// <summary>
    /// Drops the tenant's consumed and expired codes, except the emailed ones <see cref="MayEmailAsync"/>
    /// still counts. <see cref="IssueEmailedAsync"/> runs it before its transaction: inside, it would hold
    /// the rows it deleted while the void waits on a redemption's open claim, and
    /// that redemption's recovery session, minted on a second connection, prunes the same rows and waits
    /// on them. The count cannot miss a code the prune drops, because it reads later than the prune and
    /// the prune keeps every emailed code inside the window.
    /// </summary>
    private Task PruneAsync(NocturneDbContext dbContext, CancellationToken ct)
    {
        var now = Now;
        var counted = now - EmailWindow;
        return dbContext.LoginCodes
            .Where(c => (c.ConsumedAt != null || c.ExpiresAt <= now)
                && !(EmailPurposes.Contains(c.Purpose) && c.CreatedAt > counted))
            .ExecuteDeleteAsync(ct);
    }

    private async Task<MintedCode> MintAsync(
        NocturneDbContext dbContext, Guid subjectId, LoginCodePurpose purpose, string? email, CancellationToken ct)
    {
        var now = Now;

        var superseded = SupersededBy(purpose);
        if (superseded.Count > 0)
            await VoidAsync(dbContext, subjectId, superseded, ct);

        var code = _jwtService.GenerateRefreshToken();
        var entity = new LoginCodeEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CodeHash = HashUtils.Sha256Hex(code),
            Purpose = purpose,
            Email = email,
            CreatedAt = now,
            ExpiresAt = now.Add(LifetimeOf(purpose)),
        };

        dbContext.LoginCodes.Add(entity);
        await dbContext.SaveChangesAsync(ct);
        return new MintedCode(entity.Id, new LoginCode(code, entity.ExpiresAt));
    }

    private Task AuditIssueAsync(
        NocturneDbContext dbContext, Guid subjectId, LoginCodePurpose purpose, Guid codeId,
        string? ipAddress, string? userAgent, AuthAuditActor actor) =>
        _auditService.LogAsync(
            AuthAuditEventType.LoginCodeIssued, subjectId, success: true,
            ipAddress: ipAddress,
            userAgent: userAgent,
            detailsJson: JsonSerializer.Serialize(new { purpose = purpose.ToString(), code_id = codeId }),
            actor: actor,
            tenantId: dbContext.TenantIdOrNull);

    /// <inheritdoc />
    public async Task<RedeemedLoginCode?> FindLiveAsync(
        NocturneDbContext dbContext,
        string code,
        IReadOnlyCollection<LoginCodePurpose> purposes,
        Func<RedeemedLoginCode, CancellationToken, Task<bool>>? admits = null,
        CancellationToken ct = default)
    {
        var found = await LiveCodes(dbContext, code, purposes, Now)
            .Select(c => new RedeemedLoginCode(c.SubjectId, c.Purpose, c.Email) { CodeId = c.Id })
            .FirstOrDefaultAsync(ct);
        return found is not null && admits is not null && !await admits(found, ct) ? null : found;
    }

    private static IQueryable<LoginCodeEntity> LiveCodes(
        NocturneDbContext dbContext, string code, IReadOnlyCollection<LoginCodePurpose> purposes, DateTime now)
    {
        var codeHash = HashUtils.Sha256Hex(code);
        return dbContext.LoginCodes.AsNoTracking()
            .Where(c => c.CodeHash == codeHash && purposes.Contains(c.Purpose) && c.ConsumedAt == null && c.ExpiresAt > now)
            .Where(UsableByItsSubject);
    }

    /// <summary>
    /// A login code and a recovery session act as their member, signing them in or enrolling a
    /// way to, so neither works once the member is deactivated. The recovery purposes are admitted
    /// by their own rules, each of which already requires an active member. An email confirmation
    /// only confirms an address, which gives an inactive account nothing.
    /// </summary>
    internal static readonly Expression<Func<LoginCodeEntity, bool>> UsableByItsSubject = c =>
        (c.Purpose != LoginCodePurpose.Login && c.Purpose != LoginCodePurpose.RecoverySession)
        || c.Subject!.IsActive;

    /// <inheritdoc />
    public async Task<RedeemedLoginCode?> RedeemAsync(
        NocturneDbContext dbContext,
        string code,
        IReadOnlyCollection<LoginCodePurpose> purposes,
        Func<RedeemedLoginCode, CancellationToken, Task<bool>>? admits,
        Func<RedeemedLoginCode, CancellationToken, Task>? withSpend,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        var now = NocturneDbContext.AtStoredPrecision(Now);
        Guid? claimedId = null;

        var redeemed = await dbContext.ExecuteInTransactionAsync<RedeemedLoginCode?>(async token =>
        {
            var live = await LiveCodes(dbContext, code, purposes, now)
                .Select(c => new { c.Id, c.SubjectId, c.Purpose, c.Email })
                .FirstOrDefaultAsync(token);
            if (live is null)
                return null;

            var found = new RedeemedLoginCode(live.SubjectId, live.Purpose, live.Email) { CodeId = live.Id };
            if (admits is not null && !await admits(found, token))
                return null;

            // Consumption state is not a concurrency token, so the read above decides nothing: the
            // claim re-checks the code is unspent, and of two concurrent claims the row lock lets
            // one through.
            var claimed = await dbContext.LoginCodes
                .Where(c => c.Id == live.Id && c.ConsumedAt == null)
                .Where(UsableByItsSubject)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), token);
            if (claimed == 0)
                return null;
            claimedId = live.Id;

            if (withSpend is not null)
                await withSpend(found, token);

            return found;
        },
        // withSpend's writes through other contexts have committed by the time this commit fails,
        // so a commit that landed must not run them again; the claim's own stamp says it landed.
        verifySucceeded: async (found, token) =>
            found is not null && claimedId is { } id
            && await dbContext.LoginCodes.AnyAsync(c => c.Id == id && c.ConsumedAt == now, token),
        ct: ct);
        var subjectId = redeemed?.SubjectId;

        await _auditService.LogAsync(
            subjectId is null ? AuthAuditEventType.LoginHandoffFailed : AuthAuditEventType.LoginHandoff,
            subjectId, success: subjectId is not null,
            ipAddress: ipAddress,
            userAgent: userAgent,
            detailsJson: JsonSerializer.Serialize(redeemed is null
                ? (object)new { method = MethodOf(purposes) }
                : new { method = MethodOf(purposes), code_id = redeemed.CodeId }),
            tenantId: dbContext.TenantIdOrNull);

        return redeemed;
    }

    private static string MethodOf(IReadOnlyCollection<LoginCodePurpose> purposes) =>
        purposes.Contains(LoginCodePurpose.RecoverySession) ? "recovery_session"
        : purposes.Contains(LoginCodePurpose.EmailConfirm) ? "email_confirmation_link"
        : purposes.Any(SessionStartingPurposes.Contains) ? "recovery_code_link"
        : "login_code";
}
