using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Manages OIDC provider configurations stored in the database, including config-driven
/// provider syncing, discovery document caching, and client-secret encryption.
/// </summary>
/// <seealso cref="IOidcProviderService"/>
public class OidcProviderService : IOidcProviderService
{
    private readonly IOidcProviderRepository _providers;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OidcProviderService> _logger;
    private readonly IDataProtector _clientSecretProtector;
    private readonly OidcOptions _oidcOptions;

    /// <summary>
    /// Initialises a new <see cref="OidcProviderService"/>.
    /// </summary>
    /// <param name="httpClientFactory">Factory for the HTTP client used to fetch discovery documents.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="dataProtectionProvider">
    /// Used to create a scoped <see cref="IDataProtector"/> for encrypting client secrets at rest.
    /// </param>
    /// <param name="oidcOptions">Config-file OIDC provider definitions for config-managed mode.</param>
    public OidcProviderService(
        IOidcProviderRepository providers,
        IHttpClientFactory httpClientFactory,
        ILogger<OidcProviderService> logger,
        IDataProtectionProvider dataProtectionProvider,
        IOptions<OidcOptions> oidcOptions
    )
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _clientSecretProtector = dataProtectionProvider.CreateProtector(
            "Nocturne.API.Services.Auth.OidcProviderService.ClientSecret.v1"
        );
        _oidcOptions = oidcOptions.Value;
    }

    /// <inheritdoc />
    public bool IsConfigManaged => _oidcOptions.Providers.Count > 0;

    /// <summary>
    /// Syncs config-managed OIDC providers into the <c>oidc_providers</c> table so that
    /// foreign keys from <c>subject_oidc_identities</c> are satisfied.
    /// Called at startup when providers are defined in <c>appsettings.json</c> rather than the database.
    /// </summary>
    /// <param name="services">Root service provider; a new scope is created internally.</param>
    public static async Task SyncConfigProvidersAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<OidcOptions>>().Value;
        if (options.Providers.Count == 0)
            return;

        var providers = scope.ServiceProvider.GetRequiredService<IOidcProviderRepository>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<OidcProviderService>>();

        var configured = options.Providers.Select(config => new NewOidcProvider(
            config.Name,
            TypeToString(config.ProviderType),
            config.IssuerUrl.TrimEnd('/'),
            config.ClientId,
            Id: CreateDeterministicGuid(config.IssuerUrl),
            OAuth2SettingsJson: SerializeOAuth2(config.OAuth2),
            Scopes: NormalizeScopes(config.ProviderType, config.Scopes),
            DefaultRoles: config.DefaultRoles,
            IsEnabled: config.IsEnabled,
            DisplayOrder: config.DisplayOrder,
            Icon: config.Icon,
            ButtonColor: config.ButtonColor)).ToList();

        await providers.SyncConfiguredAsync(configured, DateTime.UtcNow, CancellationToken.None);
        logger.LogInformation("Synced {Count} config-managed OIDC providers to database", options.Providers.Count);
    }

    /// <inheritdoc />
    public async Task<List<OidcProvider>> GetEnabledProvidersAsync()
    {
        if (IsConfigManaged)
            return _oidcOptions.Providers
                .Where(p => p.IsEnabled)
                .OrderBy(p => p.DisplayOrder)
                .ThenBy(p => p.Name)
                .Select(MapConfigToModel)
                .ToList();

        var records = await _providers.ListAsync(enabledOnly: true, CancellationToken.None);
        return records.Select(MapToModel).ToList();
    }

    /// <inheritdoc />
    public async Task<List<OidcProvider>> GetAllProvidersAsync()
    {
        if (IsConfigManaged)
            return _oidcOptions.Providers
                .OrderBy(p => p.DisplayOrder)
                .ThenBy(p => p.Name)
                .Select(MapConfigToModel)
                .ToList();

        var records = await _providers.ListAsync(enabledOnly: false, CancellationToken.None);
        return records.Select(MapToModel).ToList();
    }

    /// <inheritdoc />
    public async Task<OidcProvider?> GetProviderByIdAsync(Guid providerId)
    {
        if (IsConfigManaged)
            return _oidcOptions.Providers
                .Select(MapConfigToModel)
                .FirstOrDefault(p => p.Id == providerId);

        var record = await _providers.GetAsync(providerId, CancellationToken.None);
        return record != null ? MapToModel(record) : null;
    }

    /// <inheritdoc />
    public async Task<OidcProvider?> GetProviderByIssuerAsync(string issuerUrl)
    {
        var record = await _providers.FindByIssuerAsync(issuerUrl, CancellationToken.None);
        return record != null ? MapToModel(record) : null;
    }

    /// <inheritdoc />
    public async Task<OidcProvider> CreateProviderAsync(OidcProvider provider)
    {
        provider.Id = await _providers.CreateAsync(
            new NewOidcProvider(
                provider.Name,
                TypeToString(provider.ProviderType),
                provider.IssuerUrl.TrimEnd('/'),
                provider.ClientId,
                OAuth2SettingsJson: SerializeOAuth2(provider.OAuth2),
                ClientSecretEncrypted: !string.IsNullOrEmpty(provider.ClientSecret) ? EncryptSecret(provider.ClientSecret) : null,
                Scopes: NormalizeScopes(provider.ProviderType, provider.Scopes),
                ClaimMappingsJson: JsonSerializer.Serialize(provider.ClaimMappings),
                DefaultRoles: provider.DefaultRoles,
                IsEnabled: provider.IsEnabled,
                DisplayOrder: provider.DisplayOrder),
            CancellationToken.None);
        return provider;
    }

    /// <inheritdoc />
    public async Task<OidcProvider?> UpdateProviderAsync(OidcProvider provider)
    {
        var updated = await _providers.UpdateAsync(
            provider.Id,
            new OidcProviderEdit(
                provider.Name,
                TypeToString(provider.ProviderType),
                SerializeOAuth2(provider.OAuth2),
                provider.IssuerUrl.TrimEnd('/'),
                provider.ClientId,
                !string.IsNullOrEmpty(provider.ClientSecret) ? EncryptSecret(provider.ClientSecret) : null,
                NormalizeScopes(provider.ProviderType, provider.Scopes),
                JsonSerializer.Serialize(provider.ClaimMappings),
                provider.DefaultRoles,
                provider.IsEnabled,
                provider.DisplayOrder),
            CancellationToken.None);
        return updated == null ? null : MapToModel(updated);
    }

    /// <inheritdoc />
    public Task<bool> DeleteProviderAsync(Guid providerId) =>
        _providers.DeleteAsync(providerId, CancellationToken.None);

    /// <inheritdoc />
    public Task<bool> EnableProviderAsync(Guid providerId) =>
        _providers.SetEnabledAsync(providerId, true, CancellationToken.None);

    /// <inheritdoc />
    public Task<bool> DisableProviderAsync(Guid providerId) =>
        _providers.SetEnabledAsync(providerId, false, CancellationToken.None);

    /// <inheritdoc />
    public async Task<OidcDiscoveryDocument?> GetDiscoveryDocumentAsync(
        Guid providerId,
        bool forceRefresh = false
    )
    {
        if (IsConfigManaged)
        {
            var configProvider = _oidcOptions.Providers
                .Select(MapConfigToModel)
                .FirstOrDefault(p => p.Id == providerId);
            if (configProvider == null)
                return null;

            // OAuth2 providers publish no discovery document; endpoints come from configuration.
            if (configProvider.ProviderType == OidcProviderType.OAuth2)
                return DiscoveryFromOAuth2Settings(configProvider.OAuth2);

            return await FetchDiscoveryDocumentAsync(configProvider.IssuerUrl);
        }

        var record = await _providers.GetAsync(providerId, CancellationToken.None);
        if (record == null)
            return null;

        // OAuth2 providers publish no discovery document; endpoints come from configuration.
        if (ParseType(record.ProviderType) == OidcProviderType.OAuth2)
            return DiscoveryFromOAuth2Settings(DeserializeOAuth2(record.OAuth2SettingsJson));

        // Check if we have a cached document that's still valid (less than 24 hours old)
        if (
            !forceRefresh
            && record.DiscoveryDocumentJson != null
            && record.DiscoveryCachedAt.HasValue
            && record.DiscoveryCachedAt.Value > DateTime.UtcNow.AddHours(-24)
        )
        {
            try
            {
                return JsonSerializer.Deserialize<OidcDiscoveryDocument>(
                    record.DiscoveryDocumentJson
                );
            }
            catch
            {
                // Fall through to fetch fresh document
            }
        }

        // Fetch fresh discovery document
        var document = await FetchDiscoveryDocumentAsync(record.IssuerUrl);
        if (document != null)
        {
            await _providers.CacheDiscoveryDocumentAsync(
                providerId, JsonSerializer.Serialize(document), DateTime.UtcNow, CancellationToken.None);
        }

        return document;
    }

    /// <inheritdoc />
    public async Task<OidcProviderTestResult> TestProviderAsync(Guid providerId)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new OidcProviderTestResult { Warnings = new List<string>() };

        var provider = await GetProviderByIdAsync(providerId);
        if (provider == null)
        {
            result.Success = false;
            result.Error = "Provider not found";
            return result;
        }

        // OAuth2 providers expose no discovery document to fetch; validate the configured endpoints instead.
        if (provider.ProviderType == OidcProviderType.OAuth2)
        {
            stopwatch.Stop();
            result.ResponseTime = stopwatch.Elapsed;
            var doc = DiscoveryFromOAuth2Settings(provider.OAuth2);
            result.DiscoveryDocument = doc;
            result.Success = doc is not null
                && !string.IsNullOrEmpty(doc.AuthorizationEndpoint)
                && !string.IsNullOrEmpty(doc.TokenEndpoint);

            if (!result.Success)
                result.Error = "OAuth2 provider is missing an authorization or token endpoint";
            else if (string.IsNullOrEmpty(doc!.UserInfoEndpoint))
                result.Warnings.Add("No userinfo endpoint configured; identity cannot be resolved");

            return result;
        }

        try
        {
            var document = await FetchDiscoveryDocumentAsync(provider.IssuerUrl);
            stopwatch.Stop();

            if (document == null)
            {
                result.Success = false;
                result.Error = "Failed to fetch discovery document";
                result.ResponseTime = stopwatch.Elapsed;
                return result;
            }

            result.Success = true;
            result.DiscoveryDocument = document;
            result.ResponseTime = stopwatch.Elapsed;

            // Add warnings for potential issues
            if (string.IsNullOrEmpty(document.UserInfoEndpoint))
            {
                result.Warnings.Add("Provider does not expose a UserInfo endpoint");
            }

            if (string.IsNullOrEmpty(document.EndSessionEndpoint))
            {
                result.Warnings.Add("Provider does not support RP-initiated logout");
            }

            // Cache the document (DB providers only)
            if (!IsConfigManaged)
            {
                await _providers.CacheDiscoveryDocumentAsync(
                    providerId, JsonSerializer.Serialize(document), DateTime.UtcNow, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            result.Success = false;
            result.Error = $"Error testing provider: {ex.Message}";
            result.ResponseTime = stopwatch.Elapsed;
            _logger.LogWarning(ex, "Error testing OIDC provider {ProviderId}", providerId);
        }

        return result;
    }

    /// <summary>
    /// Fetch the OIDC discovery document from the provider
    /// </summary>
    private async Task<OidcDiscoveryDocument?> FetchDiscoveryDocumentAsync(string issuerUrl)
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient("OidcProvider");
            var wellKnownUrl = $"{issuerUrl.TrimEnd('/')}/.well-known/openid-configuration";

            var response = await httpClient.GetAsync(wellKnownUrl);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new OidcDiscoveryDocument
            {
                Issuer = root.GetProperty("issuer").GetString() ?? issuerUrl,
                AuthorizationEndpoint = GetStringOrDefault(root, "authorization_endpoint"),
                TokenEndpoint = GetStringOrDefault(root, "token_endpoint"),
                UserInfoEndpoint = GetStringOrDefault(root, "userinfo_endpoint"),
                EndSessionEndpoint = GetStringOrDefault(root, "end_session_endpoint"),
                JwksUri = GetStringOrDefault(root, "jwks_uri"),
                IntrospectionEndpoint = GetStringOrDefault(root, "introspection_endpoint"),
                RevocationEndpoint = GetStringOrDefault(root, "revocation_endpoint"),
                ResponseTypesSupported = GetStringArrayOrDefault(root, "response_types_supported"),
                GrantTypesSupported = GetStringArrayOrDefault(root, "grant_types_supported"),
                ScopesSupported = GetStringArrayOrDefault(root, "scopes_supported"),
                IdTokenSigningAlgValuesSupported = GetStringArrayOrDefault(
                    root,
                    "id_token_signing_alg_values_supported"
                ),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to fetch OIDC discovery document from {IssuerUrl}",
                issuerUrl
            );
            return null;
        }
    }

    /// <summary>
    /// Creates a deterministic GUID from an input string using SHA-1.
    /// Used for config-defined providers so IDs survive app restarts.
    /// </summary>
    public static Guid CreateDeterministicGuid(string input)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes("nocturne-oidc-provider:" + input));
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant
        return new Guid(bytes);
    }

    private static List<string> EnsureOpenIdScope(List<string> scopes)
    {
        if (scopes.Contains("openid")) return scopes;
        return ["openid", ..scopes];
    }

    /// <summary>
    /// Applies the correct default and required scopes for the provider's protocol. OIDC providers
    /// must always request <c>openid</c>; OAuth2 providers use exactly the scopes they are configured
    /// with and must never have <c>openid</c> forced onto them.
    /// </summary>
    private static List<string> NormalizeScopes(OidcProviderType type, List<string> scopes)
    {
        if (type == OidcProviderType.OAuth2)
            return scopes;

        return scopes.Count > 0 ? EnsureOpenIdScope(scopes) : ["openid", "profile", "email"];
    }

    /// <summary>Parses the stored provider-type string, defaulting to <see cref="OidcProviderType.Oidc"/>.</summary>
    private static OidcProviderType ParseType(string? value) =>
        Enum.TryParse<OidcProviderType>(value, ignoreCase: true, out var type) ? type : OidcProviderType.Oidc;

    /// <summary>Serializes a provider type to its lower-case storage form.</summary>
    private static string TypeToString(OidcProviderType type) => type.ToString().ToLowerInvariant();

    /// <summary>
    /// Builds the discovery document for an OAuth2 provider from its configured endpoints. OAuth2
    /// providers publish no discovery document, so this stands in for one using the operator-supplied
    /// authorization, token, and userinfo endpoints.
    /// </summary>
    private static OidcDiscoveryDocument? DiscoveryFromOAuth2Settings(OAuth2ProviderSettings? settings)
    {
        if (settings is null)
            return null;

        return new OidcDiscoveryDocument
        {
            AuthorizationEndpoint = settings.AuthorizationEndpoint,
            TokenEndpoint = settings.TokenEndpoint,
            UserInfoEndpoint = settings.UserInfoEndpoint,
            JwksUri = string.Empty,
        };
    }

    private static string? SerializeOAuth2(OAuth2ProviderSettings? settings) =>
        settings is null ? null : JsonSerializer.Serialize(settings);

    private static OAuth2ProviderSettings? DeserializeOAuth2(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<OAuth2ProviderSettings>(json);
        }
        catch
        {
            return null;
        }
    }

    private static OidcProvider MapConfigToModel(OidcProviderConfig config) => new()
    {
        Id = CreateDeterministicGuid(config.IssuerUrl),
        Name = config.Name,
        ProviderType = config.ProviderType,
        OAuth2 = config.OAuth2,
        IssuerUrl = config.IssuerUrl.TrimEnd('/'),
        ClientId = config.ClientId,
        ClientSecret = config.ClientSecret,
        Scopes = NormalizeScopes(config.ProviderType, config.Scopes),
        DefaultRoles = config.DefaultRoles,
        IsEnabled = config.IsEnabled,
        DisplayOrder = config.DisplayOrder,
        Icon = config.Icon,
        ButtonColor = config.ButtonColor,
    };

    private OidcProvider MapToModel(OidcProviderRecord entity)
    {
        Dictionary<string, string>? claimMappings = null;
        if (!string.IsNullOrEmpty(entity.ClaimMappingsJson))
        {
            try
            {
                claimMappings = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    entity.ClaimMappingsJson
                );
            }
            catch
            {
                claimMappings = new Dictionary<string, string>();
            }
        }

        OidcDiscoveryDocument? discoveryDoc = null;
        if (!string.IsNullOrEmpty(entity.DiscoveryDocumentJson))
        {
            try
            {
                discoveryDoc = JsonSerializer.Deserialize<OidcDiscoveryDocument>(
                    entity.DiscoveryDocumentJson
                );
            }
            catch
            { /* ignore */
            }
        }

        return new OidcProvider
        {
            Id = entity.Id,
            Name = entity.Name,
            ProviderType = ParseType(entity.ProviderType),
            OAuth2 = DeserializeOAuth2(entity.OAuth2SettingsJson),
            IssuerUrl = entity.IssuerUrl,
            ClientId = entity.ClientId,
            ClientSecret =
                entity.ClientSecretEncrypted != null
                    ? DecryptSecret(entity.ClientSecretEncrypted)
                    : null,
            Scopes = [.. entity.Scopes],
            ClaimMappings = claimMappings ?? new Dictionary<string, string>(),
            DefaultRoles = [.. entity.DefaultRoles],
            IsEnabled = entity.IsEnabled,
            DisplayOrder = entity.DisplayOrder,
            DiscoveryDocument = discoveryDoc,
            DiscoveryCachedAt = entity.DiscoveryCachedAt,
        };
    }

    /// <summary>
    /// Encrypt a secret for storage using ASP.NET Core Data Protection
    /// </summary>
    private byte[] EncryptSecret(string secret)
    {
        var plaintext = Encoding.UTF8.GetBytes(secret);
        return _clientSecretProtector.Protect(plaintext);
    }

    /// <summary>
    /// Decrypt a stored secret
    /// </summary>
    private string DecryptSecret(byte[] encrypted)
    {
        try
        {
            var decryptedBytes = _clientSecretProtector.Unprotect(encrypted);
            return Encoding.UTF8.GetString(decryptedBytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to decrypt OIDC provider client secret; falling back to plaintext."
            );
            return Encoding.UTF8.GetString(encrypted);
        }
    }

    private static string GetStringOrDefault(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop)
            ? prop.GetString() ?? string.Empty
            : string.Empty;
    }

    private static List<string> GetStringArrayOrDefault(JsonElement element, string propertyName)
    {
        if (
            !element.TryGetProperty(propertyName, out var prop)
            || prop.ValueKind != JsonValueKind.Array
        )
            return new List<string>();

        return prop.EnumerateArray()
            .Select(e => e.GetString())
            .Where(s => s != null)
            .Cast<string>()
            .ToList();
    }
}
