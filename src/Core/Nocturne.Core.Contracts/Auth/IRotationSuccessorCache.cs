namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Short-lived store mapping a just-rotated refresh token (by hash) to its successor token.
/// An SSR web app fans out several parallel requests per navigation that all carry the same
/// refresh-token cookie; whichever request rotates first wins, and the others present a
/// token that is already revoked. Replaying the winner's successor to those losers keeps
/// them authenticated instead of rendering the user signed out. Entries expire after the
/// rotation grace period, preserving reuse detection for genuinely stale tokens.
/// </summary>
public interface IRotationSuccessorCache
{
    /// <summary>
    /// Record the successor of a rotated token for the duration of the grace period.
    /// </summary>
    /// <param name="oldTokenHash">SHA-256 hash of the rotated (now revoked) token.</param>
    /// <param name="successorToken">Plaintext successor refresh token.</param>
    /// <param name="ttl">How long replay is allowed (the rotation grace period).</param>
    /// <param name="ct">Cancellation token.</param>
    Task StoreAsync(string oldTokenHash, string successorToken, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>
    /// Look up the successor of a rotated token. Returns null when absent or expired.
    /// </summary>
    /// <param name="oldTokenHash">SHA-256 hash of the rotated token being replayed.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string?> GetAsync(string oldTokenHash, CancellationToken ct = default);
}
