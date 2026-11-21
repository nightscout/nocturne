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
    /// <param name="tenantId">The tenant the primary factor was checked against, or null on a host
    /// that resolves no tenant. The token is redeemable only on the same host.</param>
    /// <param name="admittedByInvite">Whether the primary factor admitted a non-member of
    /// <paramref name="tenantId"/> because the sign-in carried a valid invite.</param>
    Task<string> CreateStepUpTokenAsync(Guid subjectId, Guid? tenantId, bool admittedByInvite);

    /// <summary>
    /// Verifies a TOTP code for the subject a step-up token was minted for and returns that subject,
    /// or null if the token or the code is not valid. Both halves are single-use: the token is
    /// consumed, so it yields at most one session, and the code's time step is consumed, so it
    /// cannot be reused for the remainder of its acceptance window. Wrong codes are bounded per
    /// token, which a few of them spend, and per subject across every token minted for it.
    /// </summary>
    /// <exception cref="TotpStepUpLockedException">The subject has no wrong codes left until a
    /// window ends; the code was not checked.</exception>
    Task<TotpLoginResult?> VerifyStepUpAsync(string stepUpToken, string code);

    Task<List<TotpCredentialInfo>> GetCredentialsAsync(Guid subjectId);

    /// <summary>
    /// Removes one of the subject's authenticators and ends the trust of every device the subject
    /// trusted. Returns how many devices that was.
    /// </summary>
    Task<int> RemoveCredentialAsync(Guid credentialId, Guid subjectId);

    /// <summary>
    /// What a sign-in still owes after its primary factor passed for <paramref name="subjectId"/>.
    /// Both primary factors ask here, so this is the one place a trusted device stands in for a code.
    /// </summary>
    /// <param name="subjectId">The subject the primary factor just verified.</param>
    /// <param name="trustedDeviceToken">The trusted-device cookie the request carries, if any.</param>
    Task<SecondFactorRequirement> GetSecondFactorRequirementAsync(Guid subjectId, string? trustedDeviceToken);
}

/// <summary>
/// What a sign-in owes once its primary factor has passed. The default value is the one that
/// withholds the session, so an answer nobody gave never signs anyone in.
/// </summary>
public enum SecondFactorRequirement
{
    /// <summary>The subject has an authenticator and must present a code.</summary>
    CodeRequired,

    /// <summary>The subject has no authenticator, so the primary factor completes the sign-in.</summary>
    NotEnrolled,

    /// <summary>
    /// The subject has an authenticator, and the request came from a device the subject trusted
    /// after an earlier code, so no code is asked for.
    /// </summary>
    SatisfiedByTrustedDevice,
}

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
/// <param name="TenantId">The tenant the step-up token was bound to when minted, or null for a host
/// that resolves no tenant.</param>
/// <param name="AdmittedByInvite">Whether the primary factor admitted a non-member on an invite.</param>
public record TotpLoginResult(
    Guid SubjectId, string Username, string DisplayName, Guid? TenantId, bool AdmittedByInvite);

/// <summary>Summary information about a registered TOTP credential.</summary>
/// <param name="Id">The credential's ID.</param>
/// <param name="Label">User-assigned label for the authenticator, if any.</param>
/// <param name="CreatedAt">When the credential was registered.</param>
/// <param name="LastUsedAt">When the credential was last used for authentication, if ever.</param>
public record TotpCredentialInfo(Guid Id, string? Label, DateTime CreatedAt, DateTime? LastUsedAt);

/// <summary>
/// Why enrolling an authenticator was refused, at either end of the flow. The wording belongs to
/// whichever client is asking, so the server names the check that refused and nothing else.
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

    /// <summary>
    /// No passkey or linked provider is configured, so TOTP would be the only factor. Raised when
    /// setup starts, not when it completes.
    /// </summary>
    NoPrimaryFactor,

    /// <summary>
    /// The session names a subject the store no longer holds. Raised when setup starts, not when
    /// it completes.
    /// </summary>
    SubjectNotFound,
}

/// <summary>
/// Thrown when <see cref="ITotpService.CompleteSetupAsync"/> refuses an attempt. It carries only
/// the failures that completing can raise, not the ones that refuse setup before it starts.
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

/// <summary>
/// Thrown when <see cref="ITotpService.VerifyStepUpAsync"/> refuses to check a code because the
/// subject the step-up token names has had too many wrong codes recently. Only a caller who
/// completed that subject's first factor holds such a token.
/// </summary>
public class TotpStepUpLockedException : Exception
{
    /// <param name="subjectId">The subject the step-up token names.</param>
    /// <param name="retryAfter">How long until the subject's codes are checked again.</param>
    public TotpStepUpLockedException(Guid subjectId, TimeSpan retryAfter)
        : base("Too many wrong authenticator codes for this account.")
    {
        SubjectId = subjectId;
        RetryAfter = retryAfter;
    }

    /// <summary>The subject the step-up token names.</summary>
    public Guid SubjectId { get; }

    /// <summary>How long until the subject's codes are checked again.</summary>
    public TimeSpan RetryAfter { get; }
}
