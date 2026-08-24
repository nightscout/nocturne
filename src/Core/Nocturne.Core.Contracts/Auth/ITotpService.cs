using System.Text.Json.Serialization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Service for managing TOTP (Time-based One-Time Password) two-factor authentication.
/// </summary>
/// <seealso cref="IPasskeyService"/>
/// <seealso cref="IRecoveryCodeService"/>
/// <seealso cref="ISubjectService"/>
public interface ITotpService
{
    /// <summary>Generates a TOTP secret and provisioning URI for scanning by an authenticator app.</summary>
    Task<TotpSetupResult> GenerateSetupAsync(Guid subjectId, string username);

    /// <summary>Verifies a TOTP code against the pending setup challenge and registers the credential.</summary>
    /// <exception cref="TotpSetupException">The code or the challenge token was refused; the
    /// exception names which check refused it.</exception>
    Task<TotpCredentialResult> CompleteSetupAsync(string code, string label, string challengeToken);

    /// <summary>
    /// Mints a short-lived single-use token recording that a primary factor (passkey or linked
    /// provider) has just been verified for this subject. It is the only way to reach
    /// <see cref="VerifyStepUpAsync"/>, which keeps TOTP a second factor.
    /// </summary>
    /// <remarks>
    /// This method verifies nothing itself. The caller must have completed a primary factor for
    /// <paramref name="subjectId"/> in the same request before calling it — minting a token from
    /// anything less turns TOTP into a single factor for that path. Nothing in the type system
    /// enforces that, so a new call site is a security decision.
    /// </remarks>
    /// <param name="subjectId">The subject whose primary factor was just verified.</param>
    Task<string> CreateStepUpTokenAsync(Guid subjectId);

    /// <summary>
    /// Verifies a TOTP code for the subject a step-up token was minted for and returns that subject,
    /// or null if the token or the code is not valid. Both halves are single-use: the token is
    /// consumed, so it yields at most one session, and the code's time step is consumed, so it
    /// cannot be reused for the remainder of its acceptance window.
    /// </summary>
    Task<TotpLoginResult?> VerifyStepUpAsync(string stepUpToken, string code);

    /// <summary>Returns all registered TOTP credentials for the specified subject.</summary>
    Task<List<TotpCredentialInfo>> GetCredentialsAsync(Guid subjectId);

    /// <summary>Removes a TOTP credential from the specified subject.</summary>
    Task RemoveCredentialAsync(Guid credentialId, Guid subjectId);

    /// <summary>Returns the number of TOTP credentials registered to the specified subject.</summary>
    Task<int> GetCredentialCountAsync(Guid subjectId);
}

/// <summary>Result of generating a TOTP setup challenge.</summary>
/// <param name="ProvisioningUri">otpauth:// URI for scanning by an authenticator app.</param>
/// <param name="Base32Secret">Base32-encoded TOTP secret for manual entry.</param>
/// <param name="ChallengeToken">Opaque token used to correlate the challenge on completion.</param>
public record TotpSetupResult(string ProvisioningUri, string Base32Secret, string ChallengeToken);

/// <summary>Result of successfully registering a new TOTP credential.</summary>
/// <param name="CredentialId">The newly registered credential's ID.</param>
/// <param name="SubjectId">The subject the credential was registered to.</param>
public record TotpCredentialResult(Guid CredentialId, Guid SubjectId);

/// <summary>Result of a successful TOTP login verification.</summary>
/// <param name="SubjectId">The authenticated subject's ID.</param>
/// <param name="Username">The authenticated subject's username.</param>
/// <param name="DisplayName">The authenticated subject's display name.</param>
public record TotpLoginResult(Guid SubjectId, string Username, string DisplayName);

/// <summary>Summary information about a registered TOTP credential.</summary>
/// <param name="Id">The credential's ID.</param>
/// <param name="Label">User-assigned label for the authenticator, if any.</param>
/// <param name="CreatedAt">When the credential was registered.</param>
/// <param name="LastUsedAt">When the credential was last used for authentication, if ever.</param>
public record TotpCredentialInfo(Guid Id, string? Label, DateTime CreatedAt, DateTime? LastUsedAt);

/// <summary>
/// Why a TOTP setup attempt was refused. The wording belongs to whichever client is asking, so
/// the service names the check that refused and nothing else.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TotpSetupFailure>))]
public enum TotpSetupFailure
{
    /// <summary>The submitted code did not match the secret the challenge carries.</summary>
    InvalidCode,

    /// <summary>The challenge token could not be decrypted or read — tampered, or from another key ring.</summary>
    ChallengeUnreadable,

    /// <summary>The challenge token was readable but past its expiry.</summary>
    ChallengeExpired,
}

/// <summary>
/// Thrown when <see cref="ITotpService.CompleteSetupAsync"/> refuses an attempt.
/// <see cref="Failure"/> is the whole answer; the message is for logs.
/// </summary>
public class TotpSetupException : Exception
{
    /// <param name="failure">Which check refused the attempt.</param>
    /// <param name="innerException">The underlying failure, when one caused this.</param>
    public TotpSetupException(TotpSetupFailure failure, Exception? innerException = null)
        : base($"TOTP setup refused: {failure}", innerException)
    {
        Failure = failure;
    }

    /// <summary>Which check refused the attempt.</summary>
    public TotpSetupFailure Failure { get; }
}
