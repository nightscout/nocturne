namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Service for managing WebAuthn/FIDO2 passkey authentication.
/// </summary>
/// <seealso cref="ITotpService"/>
/// <seealso cref="IRecoveryCodeService"/>
/// <seealso cref="ISubjectService"/>
public interface IPasskeyService
{
    /// <summary>
    /// Explains, for a person, why a ceremony started at <paramref name="requestHost"/> cannot
    /// succeed against the configured relying-party id, or returns <c>null</c> when it can.
    /// </summary>
    /// <remarks>
    /// The rp.id is derived from the deployment's base domain, so a mismatch is a server
    /// misconfiguration the browser reports only as an opaque security error. Asking here lets a
    /// ceremony be refused with the actual reason before options are issued.
    /// </remarks>
    string? DescribeRpIdMismatch(string requestHost);

    /// <summary>
    /// Generates registration options for creating a new passkey credential. The challenge token is
    /// bound to <paramref name="flow"/> and redeems only in that flow's complete step.
    /// </summary>
    Task<PasskeyRegistrationOptions> GenerateRegistrationOptionsAsync(Guid subjectId, string username, PasskeyRegistrationFlow flow);

    /// <summary>Validates the attestation response and stores the new passkey credential.</summary>
    /// <param name="flow">
    /// The flow whose complete step redeems the challenge. A challenge minted for any other flow,
    /// or one that names no flow, is rejected.
    /// </param>
    /// <param name="expectedSubjectId">
    /// The subject the caller's flow resolved as the one enrolling. The challenge token must
    /// have been issued for this subject or the registration is rejected. Required: every flow
    /// must name its own subject, and that subject must be one the server resolved rather than
    /// one the request supplied.
    /// </param>
    /// <param name="label">Optional user-assigned label for the credential.</param>
    /// <exception cref="InvalidOperationException">
    /// The challenge token does not read, has expired, was issued for another flow or subject, or has
    /// been presented already; its options carry no challenge; the attestation response is the JSON
    /// literal <c>null</c>; or the subject holds the most credentials allowed.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">The attestation response is not JSON of its shape.</exception>
    /// <exception cref="PasskeyRegistrationRefusedException">
    /// The attestation verifier refused the attestation, or the credential is already registered.
    /// </exception>
    /// <exception cref="SetupOwnerTakenException">
    /// <paramref name="flow"/> is <see cref="PasskeyRegistrationFlow.SetupOwner"/> and another
    /// completion has claimed the setup (<see cref="IPasskeyCredentialRepository.CreateForSetupOwnerAsync"/>).
    /// </exception>
    Task<PasskeyCredentialResult> CompleteRegistrationAsync(string attestationResponseJson, string challengeToken, PasskeyRegistrationFlow flow, Guid tenantId, Guid expectedSubjectId, string? label = null);

    /// <summary>
    /// The subject a registration challenge was issued for, or null when
    /// <paramref name="challengeToken"/> is not an unexpired registration challenge this server
    /// issued for <paramref name="flow"/>.
    /// </summary>
    Guid? FindRegistrationChallengeSubject(string challengeToken, PasskeyRegistrationFlow flow);

    /// <summary>Generates assertion options for a discoverable (usernameless) login flow.</summary>
    Task<PasskeyAssertionOptions> GenerateDiscoverableAssertionOptionsAsync(Guid tenantId);

    /// <summary>Generates assertion options for a username-based login flow.</summary>
    Task<PasskeyAssertionOptions> GenerateAssertionOptionsAsync(string username, Guid tenantId);

    /// <summary>
    /// Validates the assertion response and returns the authenticated subject. The credential's
    /// holder must be an active member of <paramref name="tenantId"/>; any other holder is refused
    /// with <see cref="InvalidOperationException"/>, as an unknown credential is.
    /// </summary>
    Task<PasskeyAssertionResult> CompleteAssertionAsync(string assertionResponseJson, string challengeToken, Guid tenantId);

    Task<List<PasskeyCredentialInfo>> GetCredentialsAsync(Guid subjectId);

    Task RemoveCredentialAsync(Guid credentialId, Guid subjectId, Guid tenantId);

    /// <summary>Returns the number of passkey credentials registered to the specified subject.</summary>
    Task<int> GetCredentialCountAsync(Guid subjectId, Guid tenantId);
}

/// <summary>The flow a passkey registration challenge is minted for and redeemed in.</summary>
public enum PasskeyRegistrationFlow
{
    /// <summary>A passkey added to the caller's own account, signed in or under a recovery session.</summary>
    OwnAccount = 1,

    /// <summary>The first owner enrolled by setup.</summary>
    SetupOwner,

    /// <summary>A new account accepting a member invite.</summary>
    MemberInvite,

    /// <summary>A new account redeeming a tenant creation link.</summary>
    CreationLink,

    /// <summary>A new account filing an access request.</summary>
    AccessRequest,
}

/// <summary>WebAuthn registration options JSON and the associated challenge token for verification.</summary>
/// <param name="OptionsJson">Serialized PublicKeyCredentialCreationOptions JSON.</param>
/// <param name="ChallengeToken">Opaque token used to correlate the challenge on completion.</param>
public record PasskeyRegistrationOptions(string OptionsJson, string ChallengeToken);

/// <summary>WebAuthn assertion options JSON and the associated challenge token for verification.</summary>
/// <param name="OptionsJson">Serialized PublicKeyCredentialRequestOptions JSON.</param>
/// <param name="ChallengeToken">Opaque token used to correlate the challenge on completion.</param>
public record PasskeyAssertionOptions(string OptionsJson, string ChallengeToken);

/// <summary>Result of a successful passkey assertion (login).</summary>
/// <param name="SubjectId">The authenticated subject's ID.</param>
/// <param name="Username">The authenticated subject's username.</param>
/// <param name="DisplayName">The authenticated subject's display name.</param>
public record PasskeyAssertionResult(Guid SubjectId, string Username, string DisplayName);

/// <summary>Result of successfully registering a new passkey credential.</summary>
/// <param name="CredentialId">The newly registered credential's ID.</param>
/// <param name="SubjectId">The subject the credential was registered to.</param>
public record PasskeyCredentialResult(Guid CredentialId, Guid SubjectId);

/// <summary>Summary information about a registered passkey credential.</summary>
/// <param name="Id">The credential's ID.</param>
/// <param name="Label">User-assigned label for the credential, if any.</param>
/// <param name="CreatedAt">When the credential was registered.</param>
/// <param name="LastUsedAt">When the credential was last used for authentication, if ever.</param>
/// <param name="AaGuid">Authenticator Attestation GUID identifying the authenticator model, if available.</param>
public record PasskeyCredentialInfo(Guid Id, string? Label, DateTime CreatedAt, DateTime? LastUsedAt, Guid? AaGuid);

/// <summary>
/// Thrown when the first-run setup owner's passkey is not stored because another completion has
/// already claimed the setup (<see cref="IPasskeyCredentialRepository.CreateForSetupOwnerAsync"/>).
/// </summary>
public sealed class SetupOwnerTakenException()
    : Exception("Another completion has claimed the setup.");

/// <summary>
/// Thrown when the attestation verifier refuses a passkey registration, or the credential it
/// presents is already registered. The verifier's own failure, whatever its type, is the inner
/// exception.
/// </summary>
public sealed class PasskeyRegistrationRefusedException(string message, Exception? innerException = null)
    : Exception(message, innerException);
