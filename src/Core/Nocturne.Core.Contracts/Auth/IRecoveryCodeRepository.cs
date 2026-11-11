using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Stores recovery codes as the salted hashes the caller computes; a code is live until it is
/// used or invalidated. Codes belong to a subject, not a tenant, so every method runs as the
/// scope's reader with no pin.
/// </summary>
public interface IRecoveryCodeRepository
{
    /// <summary>Replaces every code the subject holds, live or not, with <paramref name="codeHashes"/> created at <paramref name="now"/>.</summary>
    Task ReplaceAsync(Guid subjectId, IReadOnlyList<string> codeHashes, DateTime now, CancellationToken ct);

    /// <summary>The subject's live codes in id order, so a verification walks them in one fixed order.</summary>
    Task<IReadOnlyList<RecoveryCodeHash>> GetLiveAsync(Guid subjectId, CancellationToken ct);

    /// <summary>
    /// Uses code <paramref name="codeId"/> at <paramref name="now"/>, in one statement conditioned
    /// on it still being live, so two requests presenting the same code cannot both succeed.
    /// </summary>
    Task<bool> TryConsumeAsync(Guid codeId, DateTime now, CancellationToken ct);

    /// <summary>Invalidates the subject's live codes at <paramref name="now"/> and returns how many.</summary>
    Task<int> InvalidateLiveAsync(Guid subjectId, DateTime now, CancellationToken ct);

    /// <summary>How many of the subject's codes are live.</summary>
    Task<int> CountLiveAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Whether the subject holds any code that was not invalidated, used or not.</summary>
    Task<bool> HasCodesAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Whether any of the subject's codes was invalidated.</summary>
    Task<bool> AnyInvalidatedAsync(Guid subjectId, CancellationToken ct);
}
