namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Records the passkey challenges a ceremony has been attempted with, so each completes at most
/// one. The record is instance-wide: see <c>SpentPasskeyChallengeEntity</c> for why no tenant or
/// subject owns it.
/// </summary>
public interface ISpentPasskeyChallengeRepository
{
    /// <summary>
    /// Spends <paramref name="challenge"/>: true for the first call that names it, false for every
    /// later one, across connections and instances. The spend commits on its own connection, so no
    /// caller's transaction can roll it back.
    /// </summary>
    /// <param name="challenge">The challenge as the ceremony options carry it (base64url).</param>
    /// <param name="expiresAt">When the challenge token stops being accepted (UTC).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> TrySpendAsync(string challenge, DateTime expiresAt, CancellationToken ct);

    /// <summary>
    /// Deletes at most <paramref name="batchSize"/> challenges that expired before
    /// <paramref name="cutoff"/> and returns how many it deleted.
    /// </summary>
    Task<int> DeleteExpiredAsync(DateTime cutoff, int batchSize, CancellationToken ct);
}
