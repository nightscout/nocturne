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
    /// A fixed dummy secret used for constant-time side-channel protection when a username
    /// is not found. This prevents timing attacks that could enumerate valid usernames.
    /// </summary>
    private static readonly byte[] DummySecret = new byte[20];

    private readonly NocturneDbContext _dbContext;
    private readonly IDataProtector _protector;
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

        if (!TotpHelper.Verify(payload.Secret, code))
        {
            throw new InvalidOperationException("Invalid TOTP code. Please try again.");
        }

        var entity = new TotpCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = payload.SubjectId,
            SecretKey = payload.Secret,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            CreatedAt = DateTime.UtcNow,
        };

        _dbContext.TotpCredentials.Add(entity);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "TOTP credential {CredentialId} registered for subject {SubjectId}",
            entity.Id, payload.SubjectId);

        return new TotpCredentialResult(entity.Id, payload.SubjectId);
    }

    public async Task<TotpLoginResult?> VerifyLoginAsync(string username, string code)
    {
        var subject = await _dbContext.Subjects
            .Where(s => s.Username == username && s.IsActive)
            .FirstOrDefaultAsync();

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
        foreach (var credential in credentials)
        {
            if (TotpHelper.Verify(credential.SecretKey, code))
            {
                matchedCredential = credential;
            }
        }

        if (matchedCredential is null)
        {
            return null;
        }

        matchedCredential.LastUsedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

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
            throw new InvalidOperationException("Invalid or tampered challenge token.", ex);
        }

        var payload = JsonSerializer.Deserialize<TotpChallengePayload>(json)
            ?? throw new InvalidOperationException("Failed to deserialize challenge token payload.");

        if (payload.ExpiresAt < DateTime.UtcNow)
        {
            throw new InvalidOperationException("Challenge token has expired. Please restart the setup flow.");
        }

        return payload;
    }

    private sealed class TotpChallengePayload
    {
        public byte[] Secret { get; set; } = [];
        public Guid SubjectId { get; set; }
        public DateTime ExpiresAt { get; set; }
    }
}
