using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Service for managing OIDC provider configurations
/// </summary>
public class OidcProviderService : IOidcProviderService
{
    private readonly NocturneDbContext _dbContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OidcProviderService> _logger;
    private readonly IDataProtector _clientSecretProtector;

    /// <summary>
    /// Creates a new instance of OidcProviderService
    /// </summary>
    public OidcProviderService(
        NocturneDbContext dbContext,
        IHttpClientFactory httpClientFactory,
        ILogger<OidcProviderService> logger,
        IDataProtectionProvider dataProtectionProvider
    )
    {
        _dbContext = dbContext;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _clientSecretProtector = dataProtectionProvider.CreateProtector(
            "Nocturne.API.Services.Auth.OidcProviderService.ClientSecret.v1"
        );
    }

    /// <inheritdoc />
    public async Task<List<OidcProvider>> GetEnabledProvidersAsync()
    {
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
        var entities = await _dbContext
            .OidcProviders.OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .ToListAsync();

        return entities.Select(MapToModel).ToList();
    }

    /// <inheritdoc />
    public async Task<OidcProvider?> GetProviderByIdAsync(Guid providerId)
    {
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
            IssuerUrl = provider.IssuerUrl.TrimEnd('/'),
            ClientId = provider.ClientId,
            ClientSecretEncrypted = !string.IsNullOrEmpty(provider.ClientSecret)
                ? EncryptSecret(provider.ClientSecret)
                : null,
            Scopes = provider.Scopes,
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
        entity.IssuerUrl = provider.IssuerUrl.TrimEnd('/');
        entity.ClientId = provider.ClientId;

        if (!string.IsNullOrEmpty(provider.ClientSecret))
        {
            entity.ClientSecretEncrypted = EncryptSecret(provider.ClientSecret);
        }

        entity.Scopes = provider.Scopes;
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
        var entity = await _dbContext.OidcProviders.FindAsync(providerId);
        if (entity == null)
            return null;

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

        var entity = await _dbContext.OidcProviders.FindAsync(providerId);
        if (entity == null)
        {
            result.Success = false;
            result.Error = "Provider not found";
            return result;
        }

        try
        {
            var document = await FetchDiscoveryDocumentAsync(entity.IssuerUrl);
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

            // Cache the document
            entity.DiscoveryDocumentJson = JsonSerializer.Serialize(document);
            entity.DiscoveryCachedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
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
