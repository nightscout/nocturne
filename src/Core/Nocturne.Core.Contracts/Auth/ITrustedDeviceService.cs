namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Lets a subject with an authenticator enrolled mark a browser as trusted, so later sign-ins from
/// it skip the authenticator code for a fixed period.
/// </summary>
/// <remarks>
/// A trusted device replaces the second factor only. Its token is checked after a primary factor
/// (passkey or linked provider) has passed, and only for the subject that factor named, so the
/// token on its own signs nobody in. The one caller allowed to act on <see cref="IsTrustedAsync"/>
/// is <see cref="ITotpService.GetSecondFactorRequirementAsync"/>.
/// </remarks>
/// <seealso cref="ITotpService"/>
public interface ITrustedDeviceService
{
    /// <summary>
    /// How long a device stays trusted, from <c>Oidc:Session:TrustedDeviceLifetime</c>. Zero or
    /// less turns the feature off: nothing new is trusted and nothing already trusted is honoured.
    /// </summary>
    TimeSpan Lifetime { get; }

    /// <summary>
    /// Trusts the calling device for <paramref name="subjectId"/> and returns the token for its
    /// cookie, or null when the feature is off.
    /// </summary>
    /// <remarks>
    /// This method verifies nothing itself. The caller must have verified an authenticator code for
    /// <paramref name="subjectId"/> in the same request.
    /// </remarks>
    /// <param name="subjectId">The subject whose code was just verified.</param>
    /// <param name="replacedToken">The token the device already presents, if any. A row it names
    /// for the same subject is removed, so a device is listed once.</param>
    /// <param name="userAgent">The device's user agent, for the label the subject sees.</param>
    /// <param name="ipAddress">The address the device signed in from.</param>
    Task<TrustedDeviceGrant?> TrustAsync(
        Guid subjectId, string? replacedToken, string? userAgent, string? ipAddress, CancellationToken ct = default);

    /// <summary>
    /// Whether <paramref name="token"/> names a live trusted device of <paramref name="subjectId"/>.
    /// A match records the use. A token minted for another subject never matches.
    /// </summary>
    Task<bool> IsTrustedAsync(
        Guid subjectId, string? token, string? ipAddress, CancellationToken ct = default);

    /// <summary>
    /// The subject's live trusted devices, most recently trusted first.
    /// </summary>
    /// <param name="currentToken">The token the calling device presents, to mark its own row.</param>
    Task<IReadOnlyList<TrustedDeviceInfo>> ListAsync(
        Guid subjectId, string? currentToken, CancellationToken ct = default);

    /// <summary>Stops trusting one device. False when the subject holds no such device.</summary>
    Task<bool> RevokeAsync(Guid deviceId, Guid subjectId, CancellationToken ct = default);

    /// <summary>Stops trusting every device of the subject and returns how many there were.</summary>
    Task<int> RevokeAllAsync(Guid subjectId, CancellationToken ct = default);
}

/// <summary>A newly trusted device: the token for its cookie and when the trust ends.</summary>
public record TrustedDeviceGrant(Guid DeviceId, string Token, DateTime ExpiresAt);

/// <summary>A trusted device as listed to its holder.</summary>
/// <param name="IsCurrent">Whether the caller's own device presented this row's token.</param>
public record TrustedDeviceInfo(
    Guid Id,
    string? DeviceDescription,
    string? IpAddress,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    DateTime ExpiresAt,
    bool IsCurrent);
