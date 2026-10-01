namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Service for managing single-use recovery codes for break-glass account access.
/// </summary>
/// <seealso cref="IPasskeyService"/>
/// <seealso cref="ITotpService"/>
public interface IRecoveryCodeService
{
    /// <summary>Generates a fresh set of recovery codes, replacing any existing ones.</summary>
    Task<List<string>> GenerateCodesAsync(Guid subjectId);

    /// <summary>
    /// Verifies a recovery code and consumes it so it cannot be reused. A null subject id is
    /// padded to the same hashing cost as a known one and always fails.
    /// </summary>
    Task<bool> VerifyAndConsumeAsync(Guid? subjectId, string code);

    /// <summary>Returns the number of unused recovery codes remaining for the subject.</summary>
    Task<int> GetRemainingCountAsync(Guid subjectId);

    /// <summary>Returns whether the subject has any recovery codes that have not been invalidated.</summary>
    Task<bool> HasCodesAsync(Guid subjectId);

    /// <summary>
    /// Returns whether the subject has invalidated codes and none live.
    /// </summary>
    Task<bool> WereCodesResetAsync(Guid subjectId);
}
