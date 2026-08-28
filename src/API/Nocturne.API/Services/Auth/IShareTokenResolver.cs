using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Resolves the tenant behind a public share token.
/// </summary>
/// <remarks>
/// A seam over <see cref="ShareTokenCacheService"/> for callers that only need the lookup and
/// should not have to stand up its cache and database factory — the TLS authorization endpoint
/// is one, and it is reached before any tenant is resolved.
/// </remarks>
public interface IShareTokenResolver
{
    /// <summary>
    /// Resolves the tenant owning the given share token, or <see langword="null"/> when no
    /// tenant holds it.
    /// </summary>
    Task<TenantContext?> ResolveByTokenAsync(string token);
}
