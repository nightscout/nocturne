using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Fido2NetLib.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

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

    private readonly IPasskeyCredentialRepository _passkeys;
    private readonly ISubjectRepository _subjects;
    private readonly ITenantMemberService _members;
    private readonly IFido2 _fido2;
    private readonly IDataProtector _protector;
    private readonly Fido2Configuration _fido2Config;
    private readonly ILogger<PasskeyService> _logger;
    private readonly IHostEnvironment _environment;

    /// <param name="fido2">The Fido2NetLib instance for ceremony execution.</param>
    /// <param name="dataProtectionProvider">ASP.NET Data Protection provider for encrypting challenge tokens.</param>
    /// <param name="fido2Options">FIDO2 configuration options (rpId, rpName, allowed origins, attestation preference).</param>
    /// <param name="environment">The hosting environment used to adjust origin validation in development.</param>
    public PasskeyService(
        IPasskeyCredentialRepository passkeys,
        ISubjectRepository subjects,
        ITenantMemberService members,
        IFido2 fido2,
        IDataProtectionProvider dataProtectionProvider,
        Microsoft.Extensions.Options.IOptions<Fido2Configuration> fido2Options,
        ILogger<PasskeyService> logger,
        IHostEnvironment environment)
    {
        _passkeys = passkeys ?? throw new ArgumentNullException(nameof(passkeys));
        _subjects = subjects ?? throw new ArgumentNullException(nameof(subjects));
        _members = members ?? throw new ArgumentNullException(nameof(members));
        _fido2 = fido2;
        _protector = dataProtectionProvider.CreateProtector("Nocturne.Passkey.Challenge");
        _fido2Config = fido2Options.Value;
        _logger = logger;
        _environment = environment;
    }

    /// <summary>
    /// Whether a browser at <paramref name="host"/> can complete a ceremony against the
    /// configured rpId. WebAuthn admits the rpId's own host and any host beneath it, nothing else.
    /// </summary>
    /// <remarks>
    /// Development is exempt to keep the gateway-bypassing routes usable. The dev rpId is the
    /// gateway's own host (<c>nocturne.localhost</c>), which the gateway and its tenant
    /// subdomains already satisfy; what the exemption buys is reaching the API or the web app
    /// directly on <c>localhost</c> at a port Aspire assigns.
    /// </remarks>
    private bool CanUseConfiguredRpId(string host)
    {
        var rpId = _fido2Config.ServerDomain;
        return _environment.IsDevelopment()
            || string.Equals(host, rpId, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith($".{rpId}", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public string? DescribeRpIdMismatch(string requestHost)
    {
        if (CanUseConfiguredRpId(requestHost))
            return null;

        return $"Passkeys on this server are set up for '{_fido2Config.ServerDomain}', but this "
            + $"page is served from '{requestHost}'. A browser will only create or use a passkey "
            + "when those match. Whoever runs this server needs to set "
            + $"{BaseDomainOptions.ConfigKey} to the address people browse to, then restart it.";
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
            if (CanUseConfiguredRpId(uri.Host))
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
        Guid subjectId, string username)
    {
        var existingCredentials = (await _passkeys.GetCredentialIdsAsync(subjectId, CancellationToken.None))
            .Select(id => new PublicKeyCredentialDescriptor(id))
            .ToList();

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
        var challengeToken = CreateChallengeToken(optionsJson, subjectId, ChallengePurpose.Registration);

        return new PasskeyRegistrationOptions(optionsJson, challengeToken);
    }

    public async Task<PasskeyCredentialResult> CompleteRegistrationAsync(
        string attestationResponseJson, string challengeToken, Guid tenantId, Guid expectedSubjectId,
        string? label = null)
    {
        var cookie = ReadChallengeToken(challengeToken, ChallengePurpose.Registration);

        // Checked before the ceremony so a challenge for the wrong subject is rejected outright.
        var subjectId = cookie.SubjectId
            ?? throw new InvalidOperationException("Challenge cookie missing subject ID for registration.");

        if (subjectId != expectedSubjectId)
        {
            throw new InvalidOperationException(
                "Registration challenge was not issued for the enrolling subject.");
        }

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
            IsCredentialIdUniqueToUserCallback = async (args, ct) =>
                !await _passkeys.ExistsAsync(args.CredentialId, ct),
        });

        // Enforce 20 credential cap
        var existingCount = await _passkeys.CountAsync(subjectId, CancellationToken.None);

        if (existingCount >= MaxCredentialsPerSubject)
        {
            throw new InvalidOperationException(
                $"Maximum of {MaxCredentialsPerSubject} passkey credentials per user has been reached.");
        }

        var credentialId = await _passkeys.CreateAsync(
            new NewPasskeyCredential(
                subjectId,
                credential.Id,
                credential.PublicKey,
                credential.SignCount,
                credential.Transports?.Select(t => t.ToString()).ToList() ?? [],
                string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
                // AaGuid is not directly exposed by Fido2NetLib v4 RegisteredPublicKeyCredential
                AaGuid: null,
                DateTime.UtcNow),
            CancellationToken.None);

        _logger.LogInformation(
            "Passkey credential {CredentialId} registered for subject {SubjectId} in tenant {TenantId}",
            credentialId, subjectId, tenantId);

        return new PasskeyCredentialResult(credentialId, subjectId);
    }

    public Task<PasskeyAssertionOptions> GenerateDiscoverableAssertionOptionsAsync(Guid tenantId)
    {
        var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Preferred,
        });

        var optionsJson = JsonSerializer.Serialize(options, FidoModelSerializerContext.Default.AssertionOptions);
        var challengeToken = CreateChallengeToken(optionsJson, subjectId: null, ChallengePurpose.Assertion);

        return Task.FromResult(new PasskeyAssertionOptions(optionsJson, challengeToken));
    }

    public async Task<PasskeyAssertionOptions> GenerateAssertionOptionsAsync(string username, Guid tenantId)
    {
        var subject = await _subjects.FindMemberByUsernameAsync(tenantId, username, activeOnly: true, CancellationToken.None)
            ?? throw new InvalidOperationException($"No active subject found with username '{username}' in this tenant.");

        var credentials = (await _passkeys.GetCredentialIdsAsync(subject.Id, CancellationToken.None))
            .Select(id => new PublicKeyCredentialDescriptor(id))
            .ToList();

        var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = credentials,
            UserVerification = UserVerificationRequirement.Preferred,
        });

        var optionsJson = JsonSerializer.Serialize(options, FidoModelSerializerContext.Default.AssertionOptions);
        var challengeToken = CreateChallengeToken(optionsJson, subject.Id, ChallengePurpose.Assertion);

        return new PasskeyAssertionOptions(optionsJson, challengeToken);
    }

    public async Task<PasskeyAssertionResult> CompleteAssertionAsync(
        string assertionResponseJson, string challengeToken, Guid tenantId)
    {
        var cookie = ReadChallengeToken(challengeToken, ChallengePurpose.Assertion);

        var originalOptions = JsonSerializer.Deserialize(
            cookie.OptionsJson,
            FidoModelSerializerContext.Default.AssertionOptions)
            ?? throw new InvalidOperationException("Failed to deserialize assertion options from challenge cookie.");

        var assertionResponse = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertionResponseJson)
            ?? throw new InvalidOperationException("Failed to deserialize assertion response.");

        // Find the credential in the database by raw credential ID bytes
        var stored = await _passkeys.FindByCredentialIdAsync(assertionResponse.RawId, CancellationToken.None)
            ?? throw new InvalidOperationException("Credential not found.");

        // Verify the credential's subject is a member of the current tenant
        // (the membership read excludes revoked memberships).
        if (!await _members.IsMemberAsync(stored.Credential.SubjectId, tenantId))
            throw new InvalidOperationException("Credential owner is not a member of this tenant.");

        AllowOriginFromClientData(assertionResponse.Response.ClientDataJson);
        var result = await _fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = assertionResponse,
            OriginalOptions = originalOptions,
            StoredPublicKey = stored.Credential.PublicKey,
            StoredSignatureCounter = stored.Credential.SignCount,
            IsUserHandleOwnerOfCredentialIdCallback = async (args, ct) =>
                await _passkeys.ExistsAsync(args.CredentialId, ct),
        });

        await _passkeys.RecordUseAsync(stored.Credential.Id, result.SignCount, DateTime.UtcNow, CancellationToken.None);

        var subject = stored.Holder;

        _logger.LogInformation(
            "Passkey assertion completed for subject {SubjectId} in tenant {TenantId}",
            subject.Id, tenantId);

        return new PasskeyAssertionResult(subject.Id, subject.Username ?? subject.Name, subject.Name);
    }

    public async Task<List<PasskeyCredentialInfo>> GetCredentialsAsync(Guid subjectId)
    {
        var credentials = await _passkeys.ListAsync(subjectId, CancellationToken.None);
        return credentials
            .Select(c => new PasskeyCredentialInfo(c.Id, c.Label, c.CreatedAt, c.LastUsedAt, c.AaGuid))
            .ToList();
    }

    public async Task RemoveCredentialAsync(Guid credentialId, Guid subjectId, Guid tenantId)
    {
        if (!await _passkeys.RemoveAsync(credentialId, subjectId, CancellationToken.None))
            throw new InvalidOperationException("Credential not found.");

        _logger.LogInformation(
            "Passkey credential {CredentialId} removed for subject {SubjectId} in tenant {TenantId}",
            credentialId, subjectId, tenantId);
    }

    public Task<int> GetCredentialCountAsync(Guid subjectId, Guid tenantId) =>
        _passkeys.CountAsync(subjectId, CancellationToken.None);

    private string CreateChallengeToken(string optionsJson, Guid? subjectId, string purpose)
    {
        var payload = new ChallengeCookiePayload
        {
            OptionsJson = optionsJson,
            SubjectId = subjectId,
            Purpose = purpose,
            ExpiresAt = DateTime.UtcNow.Add(ChallengeExpiry),
        };

        var json = JsonSerializer.Serialize(payload);
        return _protector.Protect(json);
    }

    private ChallengeCookiePayload ReadChallengeToken(string challengeToken, string expectedPurpose)
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

        // Registration and assertion tokens share one protector, so the purpose is checked
        // here: an assertion token is bound to the subject named in the login request, and
        // without this check it could be redeemed as a registration challenge for that subject.
        if (payload.Purpose != expectedPurpose)
        {
            throw new InvalidOperationException("Challenge token was issued for a different ceremony.");
        }

        if (payload.ExpiresAt < DateTime.UtcNow)
        {
            throw new InvalidOperationException("Challenge token has expired. Please restart the authentication flow.");
        }

        return payload;
    }

    private static class ChallengePurpose
    {
        public const string Registration = "registration";
        public const string Assertion = "assertion";
    }

    private sealed class ChallengeCookiePayload
    {
        public string OptionsJson { get; set; } = string.Empty;
        public Guid? SubjectId { get; set; }
        public string Purpose { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
    }
}
