using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Stores the OAuth tokens a client holds between requests: authorization codes, the refresh
/// tokens issued under grants, and device codes. Each is redeemed by a claim that the database
/// grants to exactly one caller, so two requests presenting the same token cannot both mint a
/// token pair. First-party session tokens are on <see cref="IFirstPartyTokenRepository"/>.
/// </summary>
/// <remarks>
/// Every method runs as the scope's reader on its tenant, except <see cref="DeleteExpiredCodesAsync"/>,
/// which pins the tenant it is given, and <see cref="RevokeRefreshTokensOfGrantsAsync"/>, which runs
/// only on a caller's unit of work. A <c>Try</c> method is one conditional statement and
/// returns whether this caller's statement changed the row; a lookup returns null for a row the
/// reader cannot see; any other write returning <c>bool</c> says whether it found a row to change.
/// </remarks>
public interface IOAuthTokenRepository
{
    /// <summary>Stores an authorization code.</summary>
    Task StoreAuthorizationCodeAsync(NewAuthorizationCode code, CancellationToken ct);

    /// <summary>
    /// Redeems the code with <paramref name="codeHash"/> if it is live at <paramref name="now"/>:
    /// exactly one caller can move its redemption stamp away from null, so a second exchange of
    /// the same code, simultaneous or later, gets <c>false</c>. Unknown, expired and redeemed codes
    /// are not told apart.
    /// </summary>
    /// <remarks>
    /// Redemption state is not a concurrency token and the unique index is on the hash, so a
    /// read-then-write check would let two simultaneous exchanges both see the code unredeemed and
    /// both mint a token pair.
    /// </remarks>
    Task<bool> TryClaimAuthorizationCodeAsync(string codeHash, DateTime now, CancellationToken ct);

    /// <summary>The code with <paramref name="codeHash"/>, redeemed or not.</summary>
    Task<AuthorizationCodeRecord?> FindAuthorizationCodeAsync(string codeHash, CancellationToken ct);

    /// <summary>Stores a refresh token and returns its id.</summary>
    Task<Guid> StoreRefreshTokenAsync(NewOAuthRefreshToken token, CancellationToken ct);

    /// <summary>The refresh token with <paramref name="tokenHash"/> and its grant.</summary>
    Task<OAuthRefreshTokenRecord?> FindRefreshTokenAsync(string tokenHash, CancellationToken ct);

    /// <summary>The refresh token with <paramref name="tokenId"/> and its grant.</summary>
    Task<OAuthRefreshTokenRecord?> GetRefreshTokenAsync(Guid tokenId, CancellationToken ct);

    /// <summary>
    /// Rotates the live token <paramref name="tokenId"/> into its stored successor
    /// <paramref name="successorId"/>: revokes it and links the successor in one statement, so
    /// exactly one of several callers refreshing with the same token rotates it, and a reader never
    /// sees the token revoked without its replacement, which is what tells a rotation from a
    /// sign-out. Returns <c>false</c> when another caller revoked it first.
    /// </summary>
    Task<bool> TryRotateRefreshTokenAsync(Guid tokenId, Guid successorId, DateTime now, CancellationToken ct);

    /// <summary>
    /// Takes one of a rotated token's <paramref name="maxRedemptions"/> grace redemptions, in one
    /// statement, so the cap holds across simultaneous presentations. Returns <c>false</c> once the
    /// cap is reached.
    /// </summary>
    Task<bool> TryRedeemRefreshTokenGraceAsync(Guid tokenId, int maxRedemptions, CancellationToken ct);

    /// <summary>Whether <paramref name="tokenId"/> is a token revoked without being rotated.</summary>
    Task<bool> IsRefreshTokenSignedOutAsync(Guid tokenId, CancellationToken ct);

    /// <summary>
    /// Revokes <paramref name="tokenId"/> at <paramref name="now"/> if it is still live. Returns
    /// <c>false</c> when it was already revoked, by a rotation or a sign-out.
    /// </summary>
    Task<bool> RevokeRefreshTokenAsync(Guid tokenId, DateTime now, CancellationToken ct);

    /// <summary>
    /// Stages, at <paramref name="now"/>, the revocation of the live refresh tokens of
    /// <paramref name="grantIds"/> on the caller's unit of work, and returns how many. The caller has
    /// staged the grants' own revocation on the same unit, so the two land in one save and no token
    /// outlives its grant or dies before it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The repository is not over a unit of work.</exception>
    Task<int> RevokeRefreshTokensOfGrantsAsync(
        IReadOnlyCollection<Guid> grantIds, DateTime now, CancellationToken ct);

    /// <summary>Whether a device code with the normalized <paramref name="userCode"/> is stored.</summary>
    Task<bool> UserCodeExistsAsync(string userCode, CancellationToken ct);

    /// <summary>Stores a device code and returns its id.</summary>
    Task<Guid> StoreDeviceCodeAsync(NewDeviceCode code, CancellationToken ct);

    /// <summary>The device code with the normalized <paramref name="userCode"/>.</summary>
    Task<DeviceCodeRecord?> FindDeviceCodeByUserCodeAsync(string userCode, CancellationToken ct);

    /// <summary>The device code with <paramref name="deviceCodeHash"/>.</summary>
    Task<DeviceCodeRecord?> FindDeviceCodeAsync(string deviceCodeHash, CancellationToken ct);

    /// <summary>
    /// Marks the device code approved by <paramref name="subjectId"/> into <paramref name="grantId"/>.
    /// Returns <c>false</c> when there is no such code to approve.
    /// </summary>
    Task<bool> ApproveDeviceCodeAsync(Guid id, Guid subjectId, Guid grantId, DateTime now, CancellationToken ct);

    /// <summary>Marks the device code denied. Returns <c>false</c> when there is no such code to deny.</summary>
    Task<bool> DenyDeviceCodeAsync(Guid id, DateTime now, CancellationToken ct);

    /// <summary>Records when the device code was last polled, and nothing else.</summary>
    Task<bool> RecordDevicePollAsync(Guid id, DateTime polledAt, CancellationToken ct);

    /// <summary>
    /// Lengthens the device code's polling interval by <paramref name="by"/> seconds in one
    /// statement, so two polls that both arrive too soon each add their share rather than one
    /// writing a stale interval over the other's.
    /// </summary>
    Task<bool> BumpDeviceCodeIntervalAsync(Guid id, int by, CancellationToken ct);

    /// <summary>
    /// Redeems the device code <paramref name="id"/> if it is not yet redeemed, in one statement, so
    /// two polls racing past the approval check cannot both mint a token pair. Returns <c>false</c>
    /// when another poll redeemed it first.
    /// </summary>
    Task<bool> TryRedeemDeviceCodeAsync(Guid id, DateTime now, CancellationToken ct);

    /// <summary>
    /// Deletes the device codes and authorization codes of <paramref name="tenantId"/> that
    /// expired before <paramref name="cutoff"/>.
    /// </summary>
    Task<ExpiredCodeDeletion> DeleteExpiredCodesAsync(Guid tenantId, DateTime cutoff, CancellationToken ct);
}
