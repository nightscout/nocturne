using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Fido2NetLib.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Implements WebAuthn/FIDO2 passkey registration and authentication ceremonies using Fido2NetLib.
/// Challenge state is carried in ASP.NET Data Protection-encrypted tokens passed via
/// request/response bodies; the server records only which challenges have been spent.
/// </summary>
/// <remarks>
/// Each ceremony is verified against a relying party of its own, which accepts the origin the
/// ceremony presents when <see cref="IsAcceptedOrigin"/> admits it. Maximum passkeys per subject
/// is capped at 20. Challenge tokens expire after 5 minutes, and each completes at most one
/// ceremony (<see cref="SpendChallengeAsync"/>).
/// </remarks>
/// <seealso cref="IPasskeyService"/>
/// <seealso cref="SubjectService"/>
public class PasskeyService : IPasskeyService
{
    private const int MaxCredentialsPerSubject = 20;
    private static readonly TimeSpan ChallengeExpiry = TimeSpan.FromMinutes(5);

    /// <summary>Why an assertion by a credential whose holder is not a member of the tenant is refused.</summary>
    internal const string OwnerNotAMemberMessage = "Credential owner is not a member of this tenant.";

    private readonly IPasskeyCredentialRepository _passkeys;
    private readonly ISpentPasskeyChallengeRepository _spentChallenges;
    private readonly ISubjectRepository _subjects;
    private readonly ITenantMemberService _members;
    private readonly Fido2 _fido2;
    private readonly IDataProtector _protector;
    private readonly IOptionsFactory<Fido2Configuration> _fido2Configs;
    private readonly Fido2Configuration _fido2Config;
    private readonly ILogger<PasskeyService> _logger;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _time;

    /// <param name="passkeys">Repository for the stored passkey credentials.</param>
    /// <param name="spentChallenges">Records the challenges a ceremony has been attempted with.</param>
    /// <param name="subjects">Repository for finding the tenant member a username names.</param>
    /// <param name="members">Service for checking that a credential's owner is a member of the tenant.</param>
    /// <param name="dataProtectionProvider">ASP.NET Data Protection provider for encrypting challenge tokens.</param>
    /// <param name="fido2Configs">Builds the relying party (rpId, rpName, configured origins) afresh for each ceremony.</param>
    /// <param name="logger">Logger for credential registration, assertion and removal, and challenge token failures.</param>
    /// <param name="environment">The hosting environment used to adjust origin validation in development.</param>
    /// <param name="timeProvider">The clock a member's end date is judged by.</param>
    public PasskeyService(
        IPasskeyCredentialRepository passkeys,
        ISpentPasskeyChallengeRepository spentChallenges,
        ISubjectRepository subjects,
        ITenantMemberService members,
        IDataProtectionProvider dataProtectionProvider,
        IOptionsFactory<Fido2Configuration> fido2Configs,
        ILogger<PasskeyService> logger,
        IHostEnvironment environment,
        TimeProvider timeProvider)
    {
        _time = timeProvider;
        _passkeys = passkeys ?? throw new ArgumentNullException(nameof(passkeys));
        _spentChallenges = spentChallenges ?? throw new ArgumentNullException(nameof(spentChallenges));
        _subjects = subjects ?? throw new ArgumentNullException(nameof(subjects));
        _members = members ?? throw new ArgumentNullException(nameof(members));
        _protector = dataProtectionProvider.CreateProtector("Nocturne.Passkey.Challenge");
        _fido2Configs = fido2Configs;
        _fido2Config = fido2Configs.Create(Options.DefaultName);
        _fido2 = new Fido2(_fido2Config);
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
        var rpId = _fido2Config.RPID;
        return _environment.IsDevelopment()
            || string.Equals(host, rpId, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith($".{rpId}", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public string? DescribeRpIdMismatch(string requestHost)
    {
        if (CanUseConfiguredRpId(requestHost))
            return null;

        return $"Passkeys on this server are set up for '{_fido2Config.RPID}', but this "
            + $"page is served from '{requestHost}'. A browser will only create or use a passkey "
            + "when those match. Whoever runs this server needs to set "
            + $"{BaseDomainOptions.ConfigKey} to the address people browse to, then restart it.";
    }

    /// <summary>
    /// Whether a ceremony may present <paramref name="origin"/>: a page on the rpId's own host or
    /// any host beneath it (tenant and share hosts are not known at startup), at any scheme and
    /// port. The configured origins are on the rpId's host, so this rule covers them too.
    /// </summary>
    private bool IsAcceptedOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && CanUseConfiguredRpId(uri.Host);

    /// <summary>
    /// The verifier for one ceremony: the configured relying party, accepting also the origin
    /// <paramref name="clientDataJson"/> names when <see cref="IsAcceptedOrigin"/> admits it.
    /// </summary>
    /// <remarks>
    /// Fido2 reads the accepted origins from the configuration it is given, so the presented
    /// origin goes into a configuration built for this ceremony alone. A client data that names no
    /// origin it can read adds nothing, and Fido2 refuses it as it refuses one with no origin.
    /// </remarks>
    private Fido2 VerifierFor(byte[]? clientDataJson)
    {
        var config = _fido2Configs.Create(Options.DefaultName);
        if (PresentedOrigin(clientDataJson) is { } origin && IsAcceptedOrigin(origin))
            config.Origins = new HashSet<string>(config.Origins) { origin };

        return new Fido2(config);
    }

    /// <summary>
    /// The <c>origin</c> string of <paramref name="clientDataJson"/>, or null when it is not a JSON
    /// object naming an origin as text: not JSON, another shape, or a string that is not valid
    /// UTF-8 or UTF-16 (which <see cref="JsonElement.GetString"/> rejects).
    /// </summary>
    private static string? PresentedOrigin(byte[]? clientDataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(clientDataJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("origin", out var origin)
                && origin.ValueKind == JsonValueKind.String
                    ? origin.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<PasskeyRegistrationOptions> GenerateRegistrationOptionsAsync(
        Guid subjectId, string username, PasskeyRegistrationFlow flow)
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
        var challengeToken = CreateChallengeToken(optionsJson, subjectId, ChallengePurpose.Registration, flow);

        return new PasskeyRegistrationOptions(optionsJson, challengeToken);
    }

    public async Task<PasskeyCredentialResult> CompleteRegistrationAsync(
        string attestationResponseJson, string challengeToken, PasskeyRegistrationFlow flow, Guid tenantId,
        Guid expectedSubjectId, string? label = null)
    {
        var cookie = ReadRegistrationChallengeToken(challengeToken, flow);

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

        await SpendChallengeAsync(originalOptions.Challenge, cookie.ExpiresAt);

        var attestationResponse = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(attestationResponseJson)
            ?? throw new InvalidOperationException("Failed to deserialize attestation response.");

        var verifier = VerifierFor(attestationResponse.Response?.ClientDataJson);
        RegisteredPublicKeyCredential credential;
        try
        {
            // Fido2 refuses a response with no authenticator response part. Uniqueness is read
            // after the call, so a failure of that read is not taken for a refusal.
            credential = await verifier.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = attestationResponse,
                OriginalOptions = originalOptions,
                IsCredentialIdUniqueToUserCallback = (_, _) => Task.FromResult(true),
            });
        }
        // The verifier parses caller-supplied bytes and lets arbitrary types out of its parsers,
        // so every failure of this call is a refused registration.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PasskeyRegistrationRefusedException("The attestation was refused.", ex);
        }

        if (await _passkeys.ExistsAsync(credential.Id, CancellationToken.None))
            throw new PasskeyRegistrationRefusedException("The credential is already registered.");

        // Enforce 20 credential cap
        var existingCount = await _passkeys.CountAsync(subjectId, CancellationToken.None);

        if (existingCount >= MaxCredentialsPerSubject)
        {
            throw new InvalidOperationException(
                $"Maximum of {MaxCredentialsPerSubject} passkey credentials per user has been reached.");
        }

        var stored = new NewPasskeyCredential(
            subjectId,
            credential.Id,
            credential.PublicKey,
            credential.SignCount,
            credential.Transports?.Select(t => t.ToString()).ToList() ?? [],
            string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            // AaGuid is not directly exposed by Fido2NetLib v4 RegisteredPublicKeyCredential
            AaGuid: null,
            DateTime.UtcNow);
        var credentialId = flow == PasskeyRegistrationFlow.SetupOwner
            ? await _passkeys.CreateForSetupOwnerAsync(tenantId, stored, CancellationToken.None)
                ?? throw new SetupOwnerTakenException()
            : await _passkeys.CreateAsync(stored, CancellationToken.None);

        _logger.LogInformation(
            "Passkey credential {CredentialId} registered for subject {SubjectId} in tenant {TenantId}",
            credentialId, subjectId, tenantId);

        return new PasskeyCredentialResult(credentialId, subjectId);
    }

    public Guid? FindRegistrationChallengeSubject(string challengeToken, PasskeyRegistrationFlow flow)
    {
        try
        {
            return ReadRegistrationChallengeToken(challengeToken, flow).SubjectId;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
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
        var subject = await _subjects.FindMemberByUsernameAsync(tenantId, username, activeOnly: true, _time.GetUtcNow().UtcDateTime, CancellationToken.None)
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

        await SpendChallengeAsync(originalOptions.Challenge, cookie.ExpiresAt);

        var assertionResponse = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertionResponseJson)
            ?? throw new InvalidOperationException("Failed to deserialize assertion response.");

        // Find the credential in the database by raw credential ID bytes
        var stored = await _passkeys.FindByCredentialIdAsync(assertionResponse.RawId, CancellationToken.None)
            ?? throw new InvalidOperationException("Credential not found.");

        // Verify the credential's subject is a member of the current tenant
        // (the membership read excludes revoked memberships).
        if (!await _members.IsMemberAsync(stored.Credential.SubjectId, tenantId))
            throw new InvalidOperationException(OwnerNotAMemberMessage);

        if (!stored.Holder.IsActive)
            throw new InvalidOperationException("Credential owner is not active.");

        // Fido2 refuses a response with no authenticator response part.
        var result = await VerifierFor(assertionResponse.Response?.ClientDataJson).MakeAssertionAsync(new MakeAssertionParams
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

    private string CreateChallengeToken(
        string optionsJson, Guid? subjectId, string purpose, PasskeyRegistrationFlow? flow = null)
    {
        var payload = new ChallengeCookiePayload
        {
            OptionsJson = optionsJson,
            SubjectId = subjectId,
            Purpose = purpose,
            Flow = flow,
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

    /// <summary>
    /// Spends the challenge a ceremony presents, or refuses the ceremony when it was spent before.
    /// </summary>
    /// <remarks>
    /// Spent on the attempt, before the response is read or any credential looked up, so a
    /// challenge buys one verification whatever its outcome and a retry starts from new options.
    /// The key is the challenge inside the token's options, which every token carries, so a token
    /// minted before the record existed is spent like any other. Options with no challenge are
    /// refused and spend nothing. A failure to record the spend is not caught: the ceremony fails
    /// as an error, not as a refusal.
    /// </remarks>
    private async Task SpendChallengeAsync(byte[]? challenge, DateTime expiresAt)
    {
        if (challenge is not { Length: > 0 })
        {
            throw new InvalidOperationException("Challenge token carries no challenge. Please restart the authentication flow.");
        }

        if (!await _spentChallenges.TrySpendAsync(
                System.Buffers.Text.Base64Url.EncodeToString(challenge), expiresAt, CancellationToken.None))
        {
            throw new InvalidOperationException("Challenge token has already been presented. Please restart the authentication flow.");
        }
    }

    /// <summary>
    /// Reads a registration challenge minted for <paramref name="flow"/>. Each flow's options step
    /// creates or resolves a subject of its own kind, and only that flow's complete step checks
    /// that kind, so a challenge redeems only in the flow that minted it.
    /// </summary>
    private ChallengeCookiePayload ReadRegistrationChallengeToken(string challengeToken, PasskeyRegistrationFlow flow)
    {
        var payload = ReadChallengeToken(challengeToken, ChallengePurpose.Registration);
        if (payload.Flow != flow)
            throw new InvalidOperationException("Registration challenge was issued for a different flow.");

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
        public PasskeyRegistrationFlow? Flow { get; set; }
        public DateTime ExpiresAt { get; set; }
    }
}
