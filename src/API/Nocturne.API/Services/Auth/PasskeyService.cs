using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Fido2NetLib.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Implements WebAuthn/FIDO2 passkey registration and authentication ceremonies using Fido2NetLib.
/// Challenge state is persisted in ASP.NET Data Protection-encrypted tokens passed via
/// request/response bodies (stateless challenge flow — no server-side session required).
/// </summary>
/// <remarks>
/// The service dynamically adds tenant subdomain origins
/// to the FIDO2 allowed-origins list when the browser's WebAuthn origin is a valid subdomain
/// of the configured <c>rpId</c>. Maximum passkeys per subject is capped at 20.
/// Challenge tokens expire after 5 minutes.
/// </remarks>
/// <seealso cref="IPasskeyService"/>
/// <seealso cref="SubjectService"/>
public class PasskeyService : IPasskeyService
{
    private const int MaxCredentialsPerSubject = 20;
    private static readonly TimeSpan ChallengeExpiry = TimeSpan.FromMinutes(5);

    private readonly NocturneDbContext _dbContext;
    private readonly IFido2 _fido2;
    private readonly IDataProtector _protector;
    private readonly Fido2Configuration _fido2Config;
    private readonly ILogger<PasskeyService> _logger;
    private readonly IHostEnvironment _environment;

    /// <summary>
    /// Initializes a new instance of <see cref="PasskeyService"/>.
    /// </summary>
    /// <param name="dbContext">The EF Core database context for passkey credential entity persistence.</param>
    /// <param name="fido2">The Fido2NetLib instance for ceremony execution.</param>
    /// <param name="dataProtectionProvider">ASP.NET Data Protection provider for encrypting challenge tokens.</param>
    /// <param name="fido2Options">FIDO2 configuration options (rpId, rpName, allowed origins, attestation preference).</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="environment">The hosting environment used to adjust origin validation in development.</param>
    public PasskeyService(
        NocturneDbContext dbContext,
        IFido2 fido2,
        IDataProtectionProvider dataProtectionProvider,
        Microsoft.Extensions.Options.IOptions<Fido2Configuration> fido2Options,
        ILogger<PasskeyService> logger,
        IHostEnvironment environment)
    {
        _dbContext = dbContext;
        _fido2 = fido2;
        _protector = dataProtectionProvider.CreateProtector("Nocturne.Passkey.Challenge");
        _fido2Config = fido2Options.Value;
        _logger = logger;
        _environment = environment;
    }

    /// <summary>
    /// Extracts the origin from the WebAuthn clientDataJSON and, if it is a
    /// subdomain of the configured rpId, adds it to the FIDO2 allowed origins.
    /// This is required for tenant subdomains where the browser
    /// origin (e.g. https://rhys.nocturne.run) isn't known at startup.
    /// </summary>
    private void AllowOriginFromClientData(byte[] clientDataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(clientDataJson);
            if (!doc.RootElement.TryGetProperty("origin", out var originProp))
                return;

            var origin = originProp.GetString();
            if (string.IsNullOrEmpty(origin) || _fido2Config.FullyQualifiedOrigins.Contains(origin))
                return;

            var uri = new Uri(origin);
            var rpId = _fido2Config.ServerDomain;
            if (_environment.IsDevelopment() ||
                uri.Host == rpId || uri.Host.EndsWith($".{rpId}", StringComparison.OrdinalIgnoreCase))
            {
                ((HashSet<string>)_fido2Config.Origins).Add(origin);

                // FullyQualifiedOrigins is lazily cached from Origins — adding to
                // Origins after the cache is built won't update it. Mutate directly.
                if (_fido2Config.FullyQualifiedOrigins is HashSet<string> fqOrigins)
                {
                    fqOrigins.Add(origin);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not extract origin from clientDataJSON");
        }
    }

    public async Task<PasskeyRegistrationOptions> GenerateRegistrationOptionsAsync(
        Guid subjectId, string username, Guid tenantId)
    {
        var existingCredentials = await _dbContext.PasskeyCredentials
            .Where(c => c.SubjectId == subjectId)
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToListAsync();

        var user = new Fido2User
        {
            Id = subjectId.ToByteArray(),
            Name = username,
            DisplayName = username,
        };

        var options = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = user,
            ExcludeCredentials = existingCredentials,
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Preferred,
                UserVerification = UserVerificationRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        var optionsJson = JsonSerializer.Serialize(options, FidoModelSerializerContext.Default.CredentialCreateOptions);
        var challengeToken = CreateChallengeToken(optionsJson, subjectId);

        return new PasskeyRegistrationOptions(optionsJson, challengeToken);
    }

    public async Task<PasskeyCredentialResult> CompleteRegistrationAsync(
        string attestationResponseJson, string challengeToken, Guid tenantId, string? label = null)
    {
        var cookie = ReadChallengeToken(challengeToken);

        var originalOptions = JsonSerializer.Deserialize(
            cookie.OptionsJson,
            FidoModelSerializerContext.Default.CredentialCreateOptions)
            ?? throw new InvalidOperationException("Failed to deserialize registration options from challenge cookie.");

        var attestationResponse = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(attestationResponseJson)
            ?? throw new InvalidOperationException("Failed to deserialize attestation response.");

        AllowOriginFromClientData(attestationResponse.Response.ClientDataJson);
        var credential = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
        {
            AttestationResponse = attestationResponse,
            OriginalOptions = originalOptions,
            IsCredentialIdUniqueToUserCallback = async (args, _) =>
            {
                var exists = await _dbContext.PasskeyCredentials
                    .AnyAsync(c => c.CredentialId == args.CredentialId);
                return !exists;
            },
        });

        var subjectId = cookie.SubjectId
            ?? throw new InvalidOperationException("Challenge cookie missing subject ID for registration.");

        // Enforce 20 credential cap
        var existingCount = await _dbContext.PasskeyCredentials
            .CountAsync(c => c.SubjectId == subjectId);

        if (existingCount >= MaxCredentialsPerSubject)
        {
            throw new InvalidOperationException(
                $"Maximum of {MaxCredentialsPerSubject} passkey credentials per user has been reached.");
        }

        var entity = new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CredentialId = credential.Id,
            PublicKey = credential.PublicKey,
            SignCount = credential.SignCount,
            Transports = credential.Transports?.Select(t => t.ToString()).ToList() ?? [],
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            // AaGuid is not directly exposed by Fido2NetLib v4 RegisteredPublicKeyCredential
            CreatedAt = DateTime.UtcNow,
        };

        _dbContext.PasskeyCredentials.Add(entity);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Passkey credential {CredentialId} registered for subject {SubjectId} in tenant {TenantId}",
            entity.Id, subjectId, tenantId);

        return new PasskeyCredentialResult(entity.Id, subjectId);
    }

    public Task<PasskeyAssertionOptions> GenerateDiscoverableAssertionOptionsAsync(Guid tenantId)
    {
        var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Preferred,
        });

        var optionsJson = JsonSerializer.Serialize(options, FidoModelSerializerContext.Default.AssertionOptions);
        var challengeToken = CreateChallengeToken(optionsJson, subjectId: null);

        return Task.FromResult(new PasskeyAssertionOptions(optionsJson, challengeToken));
    }

    public async Task<PasskeyAssertionOptions> GenerateAssertionOptionsAsync(string username, Guid tenantId)
    {
        // Look up subject by username through tenant membership
        var subject = await _dbContext.TenantMembers
            .Where(tm => tm.TenantId == tenantId)
            .Select(tm => tm.Subject!)
            .Where(s => s.Username == username && s.IsActive)
            .FirstOrDefaultAsync()
            ?? throw new InvalidOperationException($"No active subject found with username '{username}' in this tenant.");

        var credentials = await _dbContext.PasskeyCredentials
            .Where(c => c.SubjectId == subject.Id)
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToListAsync();

        var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = credentials,
            UserVerification = UserVerificationRequirement.Preferred,
        });

        var optionsJson = JsonSerializer.Serialize(options, FidoModelSerializerContext.Default.AssertionOptions);
        var challengeToken = CreateChallengeToken(optionsJson, subject.Id);

        return new PasskeyAssertionOptions(optionsJson, challengeToken);
    }

    public async Task<PasskeyAssertionResult> CompleteAssertionAsync(
        string assertionResponseJson, string challengeToken, Guid tenantId)
    {
        var cookie = ReadChallengeToken(challengeToken);

        var originalOptions = JsonSerializer.Deserialize(
            cookie.OptionsJson,
            FidoModelSerializerContext.Default.AssertionOptions)
            ?? throw new InvalidOperationException("Failed to deserialize assertion options from challenge cookie.");

        var assertionResponse = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertionResponseJson)
            ?? throw new InvalidOperationException("Failed to deserialize assertion response.");

        // Find the credential in the database by raw credential ID bytes
        var rawId = assertionResponse.RawId;
        var storedCredential = await _dbContext.PasskeyCredentials
            .Include(c => c.Subject)
            .FirstOrDefaultAsync(c => c.CredentialId == rawId)
            ?? throw new InvalidOperationException("Credential not found.");

        // Verify the credential's subject is a member of the current tenant
        // (the global query filter on TenantMemberEntity already excludes revoked memberships).
        var isMember = await _dbContext.TenantMembers
            .AnyAsync(tm => tm.TenantId == tenantId && tm.SubjectId == storedCredential.SubjectId);
        if (!isMember)
            throw new InvalidOperationException("Credential owner is not a member of this tenant.");

        AllowOriginFromClientData(assertionResponse.Response.ClientDataJson);
        var result = await _fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = assertionResponse,
            OriginalOptions = originalOptions,
            StoredPublicKey = storedCredential.PublicKey,
            StoredSignatureCounter = storedCredential.SignCount,
            IsUserHandleOwnerOfCredentialIdCallback = async (args, _) =>
            {
                return await _dbContext.PasskeyCredentials
                    .AnyAsync(c => c.CredentialId == args.CredentialId);
            },
        });

        // Update sign count and last used
        storedCredential.SignCount = result.SignCount;
        storedCredential.LastUsedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        var subject = storedCredential.Subject
            ?? throw new InvalidOperationException("Subject navigation not loaded.");

        _logger.LogInformation(
            "Passkey assertion completed for subject {SubjectId} in tenant {TenantId}",
            subject.Id, tenantId);

        return new PasskeyAssertionResult(subject.Id, subject.Username ?? subject.Name, subject.Name);
    }

    public async Task<List<PasskeyCredentialInfo>> GetCredentialsAsync(Guid subjectId, Guid tenantId)
    {
        return await _dbContext.PasskeyCredentials
            .Where(c => c.SubjectId == subjectId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new PasskeyCredentialInfo(c.Id, c.Label, c.CreatedAt, c.LastUsedAt, c.AaGuid))
            .ToListAsync();
    }

    public async Task RemoveCredentialAsync(Guid credentialId, Guid subjectId, Guid tenantId)
    {
        var credential = await _dbContext.PasskeyCredentials
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.SubjectId == subjectId)
            ?? throw new InvalidOperationException("Credential not found.");

        _dbContext.PasskeyCredentials.Remove(credential);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Passkey credential {CredentialId} removed for subject {SubjectId} in tenant {TenantId}",
            credentialId, subjectId, tenantId);
    }

    public async Task<int> GetCredentialCountAsync(Guid subjectId, Guid tenantId)
    {
        return await _dbContext.PasskeyCredentials
            .CountAsync(c => c.SubjectId == subjectId);
    }

    private string CreateChallengeToken(string optionsJson, Guid? subjectId)
    {
        var payload = new ChallengeCookiePayload
        {
            OptionsJson = optionsJson,
            SubjectId = subjectId,
            ExpiresAt = DateTime.UtcNow.Add(ChallengeExpiry),
        };

        var json = JsonSerializer.Serialize(payload);
        return _protector.Protect(json);
    }

    private ChallengeCookiePayload ReadChallengeToken(string challengeToken)
    {
        string json;
        try
        {
            json = _protector.Unprotect(challengeToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt challenge token");
            throw new InvalidOperationException("Invalid or tampered challenge token.", ex);
        }

        var payload = JsonSerializer.Deserialize<ChallengeCookiePayload>(json)
            ?? throw new InvalidOperationException("Failed to deserialize challenge token payload.");

        if (payload.ExpiresAt < DateTime.UtcNow)
        {
            throw new InvalidOperationException("Challenge token has expired. Please restart the authentication flow.");
        }

        return payload;
    }

    private sealed class ChallengeCookiePayload
    {
        public string OptionsJson { get; set; } = string.Empty;
        public Guid? SubjectId { get; set; }
        public DateTime ExpiresAt { get; set; }
    }
}
