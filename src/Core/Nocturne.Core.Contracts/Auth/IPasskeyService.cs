namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Service for managing WebAuthn/FIDO2 passkey authentication.
/// </summary>
/// <seealso cref="ITotpService"/>
/// <seealso cref="IRecoveryCodeService"/>
/// <seealso cref="ISubjectService"/>
public interface IPasskeyService
{
    /// <summary>Generates registration options for creating a new passkey credential.</summary>
    Task<PasskeyRegistrationOptions> GenerateRegistrationOptionsAsync(Guid subjectId, string username, Guid tenantId);

    /// <summary>Validates the attestation response and stores the new passkey credential.</summary>
    /// <param name="label">Optional user-assigned label for the credential.</param>
    Task<PasskeyCredentialResult> CompleteRegistrationAsync(string attestationResponseJson, string challengeToken, Guid tenantId, string? label = null);

    /// <summary>Generates assertion options for a discoverable (usernameless) login flow.</summary>
    Task<PasskeyAssertionOptions> GenerateDiscoverableAssertionOptionsAsync(Guid tenantId);

    /// <summary>Generates assertion options for a username-based login flow.</summary>
    Task<PasskeyAssertionOptions> GenerateAssertionOptionsAsync(string username, Guid tenantId);

    /// <summary>Validates the assertion response and returns the authenticated subject.</summary>
    Task<PasskeyAssertionResult> CompleteAssertionAsync(string assertionResponseJson, string challengeToken, Guid tenantId);

    /// <summary>Returns all registered passkey credentials for the specified subject.</summary>
    Task<List<PasskeyCredentialInfo>> GetCredentialsAsync(Guid subjectId, Guid tenantId);

    /// <summary>Removes a passkey credential from the specified subject.</summary>
    Task RemoveCredentialAsync(Guid credentialId, Guid subjectId, Guid tenantId);

    /// <summary>Returns the number of passkey credentials registered to the specified subject.</summary>
    Task<int> GetCredentialCountAsync(Guid subjectId, Guid tenantId);
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
