using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Resolves the tenant behind a public share token, without recording a view.
/// </summary>
/// <remarks>
/// A seam over <see cref="ShareTokenCacheService"/> for callers that only need the lookup and
/// should not have to stand up its cache and database factory — the TLS authorization endpoint
/// is one, and it is reached before any tenant is resolved.
/// <para>
/// Deliberately narrower than <see cref="ShareTokenCacheService.ResolveByTokenAsync"/>, which
/// also stamps <c>share_last_accessed_at</c>. That value is shown to the tenant owner as "Last
/// viewed", so only a caller actually serving the share may set it: a certificate-issuance probe
/// is not a view, and recording one would tell an owner their link had been opened when it had
/// not.
/// </para>
/// </remarks>
public interface IShareTokenResolver
{
    /// <summary>
    /// Resolves the tenant owning the given share token, or <see langword="null"/> when no
    /// tenant holds it. Does not record an access.
    /// </summary>
    Task<TenantContext?> ResolveWithoutRecordingAccessAsync(string token, CancellationToken ct);
}
