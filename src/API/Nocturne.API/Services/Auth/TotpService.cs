using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Implements TOTP credential management: setup, verification, and credential CRUD.
/// Challenge state (the shared secret) is persisted in ASP.NET Data Protection-encrypted tokens
/// for stateless setup flows. A constant-time dummy secret is used when a username is not found
/// to prevent username enumeration via timing attacks.
/// </summary>
/// <seealso cref="ITotpService"/>
/// <seealso cref="TotpHelper"/>
/// <seealso cref="SubjectService"/>
public class TotpService : ITotpService
{
    private static readonly TimeSpan ChallengeExpiry = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a step-up token stays valid: long enough to open an authenticator app and type a
    /// code, short enough that a leaked token is not a standing credential. Within that window the
    /// token is still only redeemable once — see <see cref="TotpStepUpTokenEntity"/>.
    /// </summary>
    private static readonly TimeSpan StepUpExpiry = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A fixed dummy secret used for constant-time side-channel protection when a username
    /// is not found. This prevents timing attacks that could enumerate valid usernames.
    /// </summary>
    private static readonly byte[] DummySecret = new byte[20];

    private readonly NocturneDbContext _dbContext;
    private readonly IDataProtector _protector;
    private readonly IDataProtector _stepUpProtector;
    private readonly ILogger<TotpService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="TotpService"/>.
    /// </summary>
    /// <param name="dbContext">The EF Core database context for TOTP credential entity persistence.</param>
    /// <param name="dataProtectionProvider">ASP.NET Data Protection provider for encrypting setup challenge tokens.</param>
    /// <param name="logger">The logger instance.</param>
    public TotpService(
        NocturneDbContext dbContext,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<TotpService> logger)
    {
        _dbContext = dbContext;
        _protector = dataProtectionProvider.CreateProtector("Nocturne.Totp.Setup");
        // A distinct purpose string keeps a setup challenge (which carries a secret the caller
        // already knows) from being redeemed as proof that a primary factor completed.
        _stepUpProtector = dataProtectionProvider.CreateProtector("Nocturne.Totp.StepUp");
        _logger = logger;
    }

    public Task<TotpSetupResult> GenerateSetupAsync(Guid subjectId, string username)
    {
        var secret = TotpHelper.GenerateSecret();
        var base32Secret = TotpHelper.ToBase32(secret);
        var provisioningUri = TotpHelper.BuildProvisioningUri(username, secret);
        var challengeToken = CreateChallengeToken(secret, subjectId);

        return Task.FromResult(new TotpSetupResult(provisioningUri, base32Secret, challengeToken));
    }

    public async Task<TotpCredentialResult> CompleteSetupAsync(string code, string label, string challengeToken)
    {
        var payload = ReadChallengeToken(challengeToken);

        if (!TotpHelper.TryVerify(payload.Secret, code, lastUsedStep: null, out var setupStep))
        {
            throw new TotpSetupException(TotpSetupFailure.InvalidCode);
        }

        var entity = new TotpCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = payload.SubjectId,
            SecretKey = payload.Secret,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            CreatedAt = DateTime.UtcNow,
            // The code proving setup is consumed, so it cannot also be used to sign in.
            LastUsedStep = setupStep,
            LastUsedAt = DateTime.UtcNow,
        };

        _dbContext.TotpCredentials.Add(entity);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "TOTP credential {CredentialId} registered for subject {SubjectId}",
            entity.Id, payload.SubjectId);

        return new TotpCredentialResult(entity.Id, payload.SubjectId);
    }

    public async Task<string> CreateStepUpTokenAsync(Guid subjectId)
    {
        // The subject lives on the row, not in the token, so the token cannot assert an identity of
        // its own and redemption reads the subject from state this service wrote.
        var entity = new TotpStepUpTokenEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(StepUpExpiry),
        };

        _dbContext.TotpStepUpTokens.Add(entity);
        await _dbContext.SaveChangesAsync();

        var payload = new TotpStepUpPayload
        {
            TokenId = entity.Id,
            ExpiresAt = entity.ExpiresAt,
        };

        return _stepUpProtector.Protect(JsonSerializer.Serialize(payload));
    }

    public async Task<TotpLoginResult?> VerifyStepUpAsync(string stepUpToken, string code)
    {
        Guid tokenId;
        try
        {
            tokenId = ReadStepUpToken(stepUpToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var stepUp = await _dbContext.TotpStepUpTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tokenId
                && t.ConsumedAt == null
                && t.ExpiresAt > DateTime.UtcNow);

        if (stepUp is null)
        {
            // An unknown, already-redeemed or expired token: verify against a dummy secret so the
            // response time does not distinguish it from a wrong code.
            TotpHelper.Verify(DummySecret, code);
            return null;
        }

        var subject = await _dbContext.Subjects
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == stepUp.SubjectId && s.IsActive);

        if (subject is null)
        {
            // Constant-time side-channel protection: verify against a dummy secret
            // so the response time is indistinguishable from a real verification
            TotpHelper.Verify(DummySecret, code);
            return null;
        }

        // The value converter decrypts secret_key on materialization, so an unresolvable payload
        // throws out of the query rather than out of a property read. Falls through to the same
        // dummy-verify path as the no-credential case, preserving the timing shape and the audit
        // record; recovery is RemoveCredentialAsync, which does not materialize the secret. One
        // unreadable row denies this subject's other credentials too — the converter runs over the
        // whole result set — and that condition needs an operator either way.
        List<TotpCredentialEntity> credentials;
        try
        {
            credentials = await _dbContext.TotpCredentials
                .AsNoTracking()
                .Where(c => c.SubjectId == subject.Id)
                .ToListAsync();
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(
                ex,
                "TOTP credentials for subject {SubjectId} could not be decrypted; treating the "
                + "attempt as failed. They must be removed and re-enrolled.",
                subject.Id);
            TotpHelper.Verify(DummySecret, code);
            return null;
        }

        if (credentials.Count == 0)
        {
            TotpHelper.Verify(DummySecret, code);
            return null;
        }

        // Verify against all credentials (don't short-circuit) to prevent
        // timing leaks that could reveal how many credentials a user has.
        TotpCredentialEntity? matchedCredential = null;
        var matchedStep = 0L;
        foreach (var credential in credentials)
        {
            if (TotpHelper.TryVerify(credential.SecretKey, code, credential.LastUsedStep, out var step))
            {
                matchedCredential = credential;
                matchedStep = step;
            }
        }

        if (matchedCredential is null)
        {
            return null;
        }

        // Consume the step-up token itself, conditionally, so the token yields one session however
        // many valid codes are presented within its window. Consumed after the code is verified, so
        // a mistyped code does not force the user back through the primary factor. No rows updated
        // means a concurrent request already redeemed it.
        var stepUpConsumed = await _dbContext.TotpStepUpTokens
            .Where(t => t.Id == stepUp.Id && t.ConsumedAt == null && t.ExpiresAt > DateTime.UtcNow)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.ConsumedAt, DateTime.UtcNow));

        if (stepUpConsumed == 0)
        {
            _logger.LogWarning(
                "Step-up token {TokenId} for subject {SubjectId} was already redeemed",
                stepUp.Id, subject.Id);
            return null;
        }

        // Consume the time step with a conditional update so two concurrent requests carrying the
        // same code cannot both succeed. No rows updated means another request already took it.
        var consumed = await _dbContext.TotpCredentials
            .Where(c => c.Id == matchedCredential.Id
                && (c.LastUsedStep == null || c.LastUsedStep < matchedStep))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.LastUsedStep, matchedStep)
                .SetProperty(c => c.LastUsedAt, DateTime.UtcNow));

        if (consumed == 0)
        {
            _logger.LogWarning(
                "TOTP code for subject {SubjectId} was already consumed for time step {TimeStep}",
                subject.Id, matchedStep);
            return null;
        }

        _logger.LogInformation(
            "TOTP verification succeeded for subject {SubjectId}",
            subject.Id);

        return new TotpLoginResult(subject.Id, subject.Username ?? subject.Name, subject.Name);
    }

    public async Task<List<TotpCredentialInfo>> GetCredentialsAsync(Guid subjectId)
    {
        return await _dbContext.TotpCredentials
            .Where(c => c.SubjectId == subjectId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new TotpCredentialInfo(c.Id, c.Label, c.CreatedAt, c.LastUsedAt))
            .ToListAsync();
    }

    public async Task RemoveCredentialAsync(Guid credentialId, Guid subjectId)
    {
        // Key-only projection, then delete a stub: loading the entity would decrypt secret_key and
        // throw, leaving an undecryptable credential undeletable — the case where removal matters
        // most, since re-enrolling is the recovery. ExecuteDeleteAsync would also skip the
        // materialization but is unsupported by the in-memory provider the unit tests use.
        var existingId = await _dbContext.TotpCredentials
            .Where(c => c.Id == credentialId && c.SubjectId == subjectId)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync();

        if (existingId is null)
            throw new InvalidOperationException("Credential not found.");

        // Reuse the tracked instance when there is one; attaching a second instance with the same
        // key throws.
        var tracked = _dbContext.ChangeTracker.Entries<TotpCredentialEntity>()
            .FirstOrDefault(e => e.Entity.Id == existingId.Value)?.Entity;

        _dbContext.TotpCredentials.Remove(tracked ?? new TotpCredentialEntity { Id = existingId.Value });
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "TOTP credential {CredentialId} removed for subject {SubjectId}",
            credentialId, subjectId);
    }

    public async Task<int> GetCredentialCountAsync(Guid subjectId)
    {
        return await _dbContext.TotpCredentials
            .CountAsync(c => c.SubjectId == subjectId);
    }

    private string CreateChallengeToken(byte[] secret, Guid subjectId)
    {
        var payload = new TotpChallengePayload
        {
            Secret = secret,
            SubjectId = subjectId,
            ExpiresAt = DateTime.UtcNow.Add(ChallengeExpiry),
        };

        var json = JsonSerializer.Serialize(payload);
        return _protector.Protect(json);
    }

    private TotpChallengePayload ReadChallengeToken(string challengeToken)
    {
        string json;
        try
        {
            json = _protector.Unprotect(challengeToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt TOTP challenge token");
            throw new TotpSetupException(TotpSetupFailure.ChallengeUnreadable, ex);
        }

        var payload = JsonSerializer.Deserialize<TotpChallengePayload>(json)
            ?? throw new TotpSetupException(TotpSetupFailure.ChallengeUnreadable);

        if (payload.ExpiresAt < DateTime.UtcNow)
        {
            throw new TotpSetupException(TotpSetupFailure.ChallengeExpired);
        }

        return payload;
    }

    /// <summary>
    /// Decrypts a step-up token and returns the id of the row it refers to. The row is the
    /// authority on the subject, the expiry and whether the token is still redeemable; the expiry
    /// carried in the payload only avoids a query for a token that is already stale.
    /// </summary>
    private Guid ReadStepUpToken(string stepUpToken)
    {
        string json;
        try
        {
            json = _stepUpProtector.Unprotect(stepUpToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt TOTP step-up token");
            throw new InvalidOperationException("Invalid or tampered step-up token.", ex);
        }

        var payload = JsonSerializer.Deserialize<TotpStepUpPayload>(json)
            ?? throw new InvalidOperationException("Failed to deserialize step-up token payload.");

        if (payload.ExpiresAt < DateTime.UtcNow)
        {
            throw new InvalidOperationException("Step-up token has expired.");
        }

        if (payload.TokenId == Guid.Empty)
        {
            throw new InvalidOperationException("Step-up token carries no token id.");
        }

        return payload.TokenId;
    }

    private sealed class TotpChallengePayload
    {
        public byte[] Secret { get; set; } = [];
        public Guid SubjectId { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    private sealed class TotpStepUpPayload
    {
        /// <summary>The <c>totp_step_up_tokens</c> row this token refers to.</summary>
        public Guid TokenId { get; set; }
        public DateTime ExpiresAt { get; set; }
    }
}
