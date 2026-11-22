namespace Nocturne.API.Services.Auth;

/// <summary>
/// How long an access token minted from a grant lives: its usual lifetime, cut short so it ends no
/// later than the grant. A grant whose expiry is lowered later is refused at the new date by
/// <see cref="Core.Contracts.Auth.IOAuthGrantService.IsGrantLiveAsync"/>; the cut keeps the token's
/// own expiry from promising more than the grant gives.
/// </summary>
internal static class GrantTokenLifetime
{
    /// <summary>
    /// The lesser of <paramref name="usual"/> and what is left of a grant expiring at
    /// <paramref name="grantExpiresAt"/>, never negative.
    /// </summary>
    public static TimeSpan Of(TimeSpan usual, DateTime? grantExpiresAt, DateTimeOffset now)
    {
        if (grantExpiresAt is not { } end)
            return usual;

        var left = DateTime.SpecifyKind(end, DateTimeKind.Utc) - now.UtcDateTime;
        return left < TimeSpan.Zero ? TimeSpan.Zero : left < usual ? left : usual;
    }
}
