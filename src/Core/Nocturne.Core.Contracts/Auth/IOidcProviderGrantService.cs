using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Keeps and revokes the grant an identity provider holds for a linked identity, for a provider
/// that asks to be told when its user leaves (Sign in with Apple). Apple ends its record of the
/// user's consent only when the refresh token it issued is revoked, so the token is kept, encrypted,
/// on the identity until the identity goes.
/// </summary>
public interface IOidcProviderGrantService
{
    /// <summary>
    /// Keeps <paramref name="refreshToken"/> on the identity <paramref name="provider"/> knows as
    /// <paramref name="oidcSubjectId"/>, replacing an earlier one. Does nothing for a provider whose
    /// grants are not revoked, or when no token was issued.
    /// </summary>
    Task KeepAsync(OidcProvider provider, string oidcSubjectId, string? refreshToken, CancellationToken ct = default);

    /// <summary>
    /// The grants kept for <paramref name="subjectId"/>'s identities, or only for
    /// <paramref name="identityId"/> when it is given. Read before the identities are removed,
    /// since removing them removes the tokens.
    /// </summary>
    Task<IReadOnlyList<HeldProviderGrant>> CaptureAsync(
        Guid subjectId, Guid? identityId = null, CancellationToken ct = default);

    /// <summary>
    /// Revokes each grant at its provider. A failure is logged, not thrown: the identity is already
    /// gone here, and the provider ends the grant itself when the token expires.
    /// </summary>
    Task RevokeAsync(IReadOnlyCollection<HeldProviderGrant> grants, CancellationToken ct = default);
}

/// <summary>A provider refresh token kept for a linked identity, decrypted.</summary>
/// <param name="ProviderId">The provider that issued the token.</param>
/// <param name="RefreshToken">The token to revoke.</param>
public sealed record HeldProviderGrant(Guid ProviderId, string RefreshToken);
