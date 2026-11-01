using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Implements TOTP credential management: setup, verification, and credential CRUD.
/// Challenge state (the shared secret) is persisted in ASP.NET Data Protection-encrypted tokens
/// for stateless setup flows. A constant-time dummy secret is used when a username is not found
/// to prevent username enumeration via timing attacks.
/// </summary>
/// <remarks>
/// Credentials and subjects are read through their repositories; the step-up tokens, which are
/// this service's own single-use state, are still read off a context.
/// </remarks>
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
    internal static readonly TimeSpan StepUpExpiry = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A fixed dummy secret used for constant-time side-channel protection when a username
    /// is not found. This prevents timing attacks that could enumerate valid usernames.
    /// </summary>
    private static readonly byte[] DummySecret = new byte[20];

    private readonly INocturneDbContextFactory _contexts;
    private readonly ITotpCredentialRepository _credentials;
    private readonly ISubjectRepository _subjects;
    private readonly IDataProtector _protector;
    private readonly IDataProtector _stepUpProtector;
    private readonly ILogger<TotpService> _logger;

    /// <param name="dataProtectionProvider">ASP.NET Data Protection provider for encrypting setup challenge tokens.</param>
    public TotpService(
        INocturneDbContextFactory contexts,
        ITotpCredentialRepository credentials,
        ISubjectRepository subjects,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<TotpService> logger)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _subjects = subjects ?? throw new ArgumentNullException(nameof(subjects));
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

        var now = DateTime.UtcNow;
        var credentialId = await _credentials.CreateAsync(
            new NewTotpCredential(
                payload.SubjectId,
                payload.Secret,
                string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
                now,
                // The code proving setup is consumed, so it cannot also be used to sign in.
                LastUsedStep: setupStep,
                LastUsedAt: now),
            CancellationToken.None);

        _logger.LogInformation(
            "TOTP credential {CredentialId} registered for subject {SubjectId}",
            credentialId, payload.SubjectId);

        return new TotpCredentialResult(credentialId, payload.SubjectId);
    }

    public async Task<string> CreateStepUpTokenAsync(Guid subjectId, Guid? tenantId, bool admittedByInvite)
    {
        await using var db = await _contexts.CreateAsync();
        // The subject lives on the row, not in the token, so the token cannot assert an identity of
        // its own and redemption reads the subject from state this service wrote.
        var entity = new TotpStepUpTokenEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            BoundTenantId = tenantId,
            AdmittedByInvite = admittedByInvite,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(StepUpExpiry),
        };

        db.TotpStepUpTokens.Add(entity);
        await db.SaveChangesAsync();

        var payload = new TotpStepUpPayload
        {
            TokenId = entity.Id,
            ExpiresAt = entity.ExpiresAt,
        };

        return _stepUpProtector.Protect(JsonSerializer.Serialize(payload));
    }

    public async Task<TotpLoginResult?> VerifyStepUpAsync(string stepUpToken, string code)
    {
        await using var db = await _contexts.CreateAsync();
        Guid tokenId;
        try
        {
            tokenId = ReadStepUpToken(stepUpToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var stepUp = await db.TotpStepUpTokens
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

        var subject = await _subjects.GetAsync(stepUp.SubjectId, CancellationToken.None);

        if (subject is not { IsActive: true })
        {
            // Constant-time side-channel protection: verify against a dummy secret
            // so the response time is indistinguishable from a real verification
            TotpHelper.Verify(DummySecret, code);
            return null;
        }

        // An unresolvable secret throws out of the read (see
        // ITotpCredentialRepository.ListSecretsAsync). Falls through to the same dummy-verify path
        // as the no-credential case, preserving the timing shape and the audit record; recovery is
        // RemoveCredentialAsync, which does not read the secret. One unreadable row denies this
        // subject's other credentials too, and that condition needs an operator either way.
        IReadOnlyList<TotpCredentialSecret> credentials;
        try
        {
            credentials = await _credentials.ListSecretsAsync(subject.Id, CancellationToken.None);
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
        TotpCredentialSecret? matchedCredential = null;
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
        var stepUpConsumed = await db.TotpStepUpTokens
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

        // Consume the time step conditionally so two concurrent requests carrying the same code
        // cannot both succeed. False means another request already took it.
        if (!await _credentials.TryConsumeStepAsync(matchedCredential.Id, matchedStep, DateTime.UtcNow, CancellationToken.None))
        {
            _logger.LogWarning(
                "TOTP code for subject {SubjectId} was already consumed for time step {TimeStep}",
                subject.Id, matchedStep);
            return null;
        }

        _logger.LogInformation(
            "TOTP verification succeeded for subject {SubjectId}",
            subject.Id);

        return new TotpLoginResult(
            subject.Id, subject.Username ?? subject.Name, subject.Name,
            stepUp.BoundTenantId, stepUp.AdmittedByInvite);
    }

    public async Task<List<TotpCredentialInfo>> GetCredentialsAsync(Guid subjectId)
    {
        var credentials = await _credentials.ListAsync(subjectId, CancellationToken.None);
        return credentials.Select(c => new TotpCredentialInfo(c.Id, c.Label, c.CreatedAt, c.LastUsedAt)).ToList();
    }

    public async Task RemoveCredentialAsync(Guid credentialId, Guid subjectId)
    {
        if (!await _credentials.RemoveAsync(credentialId, subjectId, CancellationToken.None))
            throw new InvalidOperationException("Credential not found.");

        _logger.LogInformation(
            "TOTP credential {CredentialId} removed for subject {SubjectId}",
            credentialId, subjectId);
    }

    public Task<int> GetCredentialCountAsync(Guid subjectId) =>
        _credentials.CountAsync(subjectId, CancellationToken.None);

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
