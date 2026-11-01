namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// A row of <c>oidc_providers</c> as stored: the client secret as ciphertext, the OAuth2 settings,
/// claim mappings and discovery document as the JSON they are kept in. The service that owns the
/// key and the shapes does the encrypting and the parsing.
/// </summary>
public sealed record OidcProviderRecord(
    Guid Id,
    string Name,
    string ProviderType,
    string? OAuth2SettingsJson,
    string IssuerUrl,
    string ClientId,
    byte[]? ClientSecretEncrypted,
    string? DiscoveryDocumentJson,
    DateTime? DiscoveryCachedAt,
    IReadOnlyList<string> Scopes,
    string ClaimMappingsJson,
    IReadOnlyList<string> DefaultRoles,
    bool IsEnabled,
    int DisplayOrder,
    string? Icon,
    string? ButtonColor,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// A provider to store. <paramref name="Id"/> is given for a config-managed provider, whose id is
/// derived from its issuer so it survives restarts; null lets the store choose one.
/// </summary>
public sealed record NewOidcProvider(
    string Name,
    string ProviderType,
    string IssuerUrl,
    string ClientId,
    Guid? Id = null,
    string? OAuth2SettingsJson = null,
    byte[]? ClientSecretEncrypted = null,
    IReadOnlyList<string>? Scopes = null,
    string? ClaimMappingsJson = null,
    IReadOnlyList<string>? DefaultRoles = null,
    bool IsEnabled = true,
    int DisplayOrder = 0,
    string? Icon = null,
    string? ButtonColor = null);

/// <summary>An administrator's edit of a provider. A null <paramref name="ClientSecretEncrypted"/> keeps the stored secret.</summary>
public sealed record OidcProviderEdit(
    string Name,
    string ProviderType,
    string? OAuth2SettingsJson,
    string IssuerUrl,
    string ClientId,
    byte[]? ClientSecretEncrypted,
    IReadOnlyList<string> Scopes,
    string ClaimMappingsJson,
    IReadOnlyList<string> DefaultRoles,
    bool IsEnabled,
    int DisplayOrder);

/// <summary>A global role to store.</summary>
public sealed record NewRole(string Name, string? Description, IReadOnlyList<string> Permissions, bool IsSystemRole);
