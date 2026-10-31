namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// A client to register. The tenant is the context's; <see cref="ClientId"/> is the caller's, and
/// <see cref="IsKnown"/> is what the caller verified against <see cref="KnownOAuthClients"/>.
/// </summary>
public sealed record NewOAuthClient(
    string ClientId,
    string? SoftwareId,
    string? ClientName,
    string? ClientUri,
    string? LogoUri,
    string? DisplayName,
    bool IsKnown,
    IReadOnlyList<string> RedirectUris,
    string? CreatedFromIp = null);

/// <summary>A registered OAuth client.</summary>
/// <param name="RedirectUris">Empty when the stored list is blank or unreadable.</param>
public sealed record OAuthClientRecord(
    Guid Id,
    Guid TenantId,
    string ClientId,
    string? SoftwareId,
    string? ClientName,
    string? ClientUri,
    string? LogoUri,
    string? CreatedFromIp,
    string? DisplayName,
    bool IsKnown,
    IReadOnlyList<string> RedirectUris,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>The outcome of <c>IOAuthClientRepository.FindOrCreateWithRedirectUriAsync</c>.</summary>
/// <param name="ClientId">The client's <c>client_id</c>.</param>
/// <param name="Registered">
/// Whether the redirect URI is registered on the client; false only when the client was full.
/// </param>
/// <param name="RedirectUriCount">How many redirect URIs the client holds.</param>
public sealed record OAuthClientRedirectUriRegistration(string ClientId, bool Registered, int RedirectUriCount);
