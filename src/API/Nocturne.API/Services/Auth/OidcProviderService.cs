using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Manages OIDC provider configurations stored in the database, including config-driven
/// provider syncing, discovery document caching, and client-secret encryption.
/// </summary>
/// <seealso cref="IOidcProviderService"/>
public class OidcProviderService : IOidcProviderService
{
    private readonly NocturneDbContext _dbContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OidcProviderService> _logger;
    private readonly IDataProtector _clientSecretProtector;
    private readonly OidcOptions _oidcOptions;

    /// <summary>
    /// Initialises a new <see cref="OidcProviderService"/>.
    /// </summary>
    /// <param name="dbContext">Database context for OIDC provider records.</param>
    /// <param name="httpClientFactory">Factory for the HTTP client used to fetch discovery documents.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="dataProtectionProvider">
    /// Used to create a scoped <see cref="IDataProtector"/> for encrypting client secrets at rest.
    /// </param>
    /// <param name="oidcOptions">Config-file OIDC provider definitions for config-managed mode.</param>
    public OidcProviderService(
        NocturneDbContext dbContext,
        IHttpClientFactory httpClientFactory,
        ILogger<OidcProviderService> logger,
        IDataProtectionProvider dataProtectionProvider,
        IOptions<OidcOptions> oidcOptions
    )
    {
        _dbContext = dbContext;
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

        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<OidcProviderService>>();

        foreach (var config in options.Providers)
        {
            var id = CreateDeterministicGuid(config.IssuerUrl);
            var existing = await db.OidcProviders.FindAsync(id);
            if (existing != null)
            {
                existing.Name = config.Name;
                existing.ProviderType = TypeToString(config.ProviderType);
                existing.OAuth2SettingsJson = SerializeOAuth2(config.OAuth2);
                existing.IssuerUrl = config.IssuerUrl.TrimEnd('/');
                existing.ClientId = config.ClientId;
                existing.IsEnabled = config.IsEnabled;
                existing.DisplayOrder = config.DisplayOrder;
                existing.Scopes = NormalizeScopes(config.ProviderType, config.Scopes);
                existing.DefaultRoles = config.DefaultRoles;
                existing.Icon = config.Icon;
                existing.ButtonColor = config.ButtonColor;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                db.OidcProviders.Add(new OidcProviderEntity
                {
                    Id = id,
                    Name = config.Name,
                    ProviderType = TypeToString(config.ProviderType),
                    OAuth2SettingsJson = SerializeOAuth2(config.OAuth2),
                    IssuerUrl = config.IssuerUrl.TrimEnd('/'),
                    ClientId = config.ClientId,
                    IsEnabled = config.IsEnabled,
                    DisplayOrder = config.DisplayOrder,
                    Scopes = NormalizeScopes(config.ProviderType, config.Scopes),
                    DefaultRoles = config.DefaultRoles,
                    Icon = config.Icon,
                    ButtonColor = config.ButtonColor,
                });
            }
        }

        await db.SaveChangesAsync();
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

        var entities = await _dbContext
            .OidcProviders.Where(p => p.IsEnabled)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .ToListAsync();

        return entities.Select(MapToModel).ToList();
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

        var entities = await _dbContext
            .OidcProviders.OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .ToListAsync();

        return entities.Select(MapToModel).ToList();
    }

    /// <inheritdoc />
    public async Task<OidcProvider?> GetProviderByIdAsync(Guid providerId)
    {
        if (IsConfigManaged)
            return _oidcOptions.Providers
                .Select(MapConfigToModel)
                .FirstOrDefault(p => p.Id == providerId);

        var entity = await _dbContext.OidcProviders.FindAsync(providerId);
        return entity != null ? MapToModel(entity) : null;
    }

    /// <inheritdoc />
    public async Task<OidcProvider?> GetProviderByIssuerAsync(string issuerUrl)
    {
        // Normalize the issuer URL (remove trailing slash)
        var normalizedIssuer = issuerUrl.TrimEnd('/');

        var entity = await _dbContext.OidcProviders.FirstOrDefaultAsync(p =>
            p.IssuerUrl == normalizedIssuer
            || p.IssuerUrl == normalizedIssuer + "/"
            || p.IssuerUrl.TrimEnd('/') == normalizedIssuer
        );

        return entity != null ? MapToModel(entity) : null;
    }

    /// <inheritdoc />
    public async Task<OidcProvider> CreateProviderAsync(OidcProvider provider)
    {
        var entity = new OidcProviderEntity
        {
            Name = provider.Name,
            ProviderType = TypeToString(provider.ProviderType),
            OAuth2SettingsJson = SerializeOAuth2(provider.OAuth2),
            IssuerUrl = provider.IssuerUrl.TrimEnd('/'),
            ClientId = provider.ClientId,
            ClientSecretEncrypted = !string.IsNullOrEmpty(provider.ClientSecret)
                ? EncryptSecret(provider.ClientSecret)
                : null,
            Scopes = NormalizeScopes(provider.ProviderType, provider.Scopes),
            ClaimMappingsJson = JsonSerializer.Serialize(provider.ClaimMappings),
            DefaultRoles = provider.DefaultRoles,
            IsEnabled = provider.IsEnabled,
            DisplayOrder = provider.DisplayOrder,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _dbContext.OidcProviders.Add(entity);
        await _dbContext.SaveChangesAsync();

        provider.Id = entity.Id;
        return provider;
    }

    /// <inheritdoc />
    public async Task<OidcProvider?> UpdateProviderAsync(OidcProvider provider)
    {
        var entity = await _dbContext.OidcProviders.FindAsync(provider.Id);
        if (entity == null)
            return null;

        entity.Name = provider.Name;
        entity.ProviderType = TypeToString(provider.ProviderType);
        entity.OAuth2SettingsJson = SerializeOAuth2(provider.OAuth2);
        entity.IssuerUrl = provider.IssuerUrl.TrimEnd('/');
        entity.ClientId = provider.ClientId;

        if (!string.IsNullOrEmpty(provider.ClientSecret))
        {
            entity.ClientSecretEncrypted = EncryptSecret(provider.ClientSecret);
        }

        entity.Scopes = NormalizeScopes(provider.ProviderType, provider.Scopes);
        entity.ClaimMappingsJson = JsonSerializer.Serialize(provider.ClaimMappings);
        entity.DefaultRoles = provider.DefaultRoles;
        entity.IsEnabled = provider.IsEnabled;
        entity.DisplayOrder = provider.DisplayOrder;
        entity.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();
        return MapToModel(entity);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteProviderAsync(Guid providerId)
    {
        var entity = await _dbContext.OidcProviders.FindAsync(providerId);
        if (entity == null)
            return false;

        _dbContext.OidcProviders.Remove(entity);
        await _dbContext.SaveChangesAsync();
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> EnableProviderAsync(Guid providerId)
    {
        var entity = await _dbContext.OidcProviders.FindAsync(providerId);
        if (entity == null)
            return false;

        entity.IsEnabled = true;
        entity.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DisableProviderAsync(Guid providerId)
    {
        var entity = await _dbContext.OidcProviders.FindAsync(providerId);
        if (entity == null)
            return false;

        entity.IsEnabled = false;
        entity.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
        return true;
    }

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

        var entity = await _dbContext.OidcProviders.FindAsync(providerId);
        if (entity == null)
            return null;

        // OAuth2 providers publish no discovery document; endpoints come from configuration.
        if (ParseType(entity.ProviderType) == OidcProviderType.OAuth2)
            return DiscoveryFromOAuth2Settings(DeserializeOAuth2(entity.OAuth2SettingsJson));

        // Check if we have a cached document that's still valid (less than 24 hours old)
        if (
            !forceRefresh
            && entity.DiscoveryDocumentJson != null
            && entity.DiscoveryCachedAt.HasValue
            && entity.DiscoveryCachedAt.Value > DateTime.UtcNow.AddHours(-24)
        )
        {
            try
            {
                return JsonSerializer.Deserialize<OidcDiscoveryDocument>(
                    entity.DiscoveryDocumentJson
                );
            }
            catch
            {
                // Fall through to fetch fresh document
            }
        }

        // Fetch fresh discovery document
        var document = await FetchDiscoveryDocumentAsync(entity.IssuerUrl);
        if (document != null)
        {
            entity.DiscoveryDocumentJson = JsonSerializer.Serialize(document);
            entity.DiscoveryCachedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
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
                var entity = await _dbContext.OidcProviders.FindAsync(providerId);
                if (entity != null)
                {
                    entity.DiscoveryDocumentJson = JsonSerializer.Serialize(document);
                    entity.DiscoveryCachedAt = DateTime.UtcNow;
                    await _dbContext.SaveChangesAsync();
                }
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

    /// <summary>
    /// Map entity to domain model
    /// </summary>
    private OidcProvider MapToModel(OidcProviderEntity entity)
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
            Scopes = entity.Scopes,
            ClaimMappings = claimMappings ?? new Dictionary<string, string>(),
            DefaultRoles = entity.DefaultRoles,
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
