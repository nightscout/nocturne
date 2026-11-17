using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="IOidcProviderRepository"/> over <see cref="NocturneDbContext"/>: one context per
/// call, saving each write, or the caller's <see cref="DbUnitOfWork"/> when constructed over one.
/// Writes are tracked reads and saves.
/// </summary>
public class OidcProviderRepository : IOidcProviderRepository
{
    private readonly INocturneDbContextFactory? _contexts;
    private readonly DbUnitOfWork? _unit;

    /// <summary>A repository whose calls each open their own context and save what they write.</summary>
    public OidcProviderRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <summary>
    /// A repository whose calls run on <paramref name="unit"/>, which the caller owns and saves, so
    /// a provider row created for a provisioned owner's identity lands with the owner.
    /// </summary>
    public OidcProviderRepository(DbUnitOfWork unit)
    {
        _unit = unit ?? throw new ArgumentNullException(nameof(unit));
    }

    private ValueTask<DbContextLease> OpenAsync(CancellationToken ct) => DbContextLease.OpenAsync(_unit, _contexts, ct);

    private Task SaveAsync(NocturneDbContext db, CancellationToken ct) =>
        _unit is null ? db.SaveChangesAsync(ct) : Task.CompletedTask;

    private IQueryable<OidcProviderEntity> Providers(NocturneDbContext db) =>
        _unit is null ? db.OidcProviders.AsNoTracking() : db.OidcProviders;

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<OidcProviderRecord>> ListAsync(bool enabledOnly, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var providers = Providers(lease.Context);
        if (enabledOnly)
            providers = providers.Where(p => p.IsEnabled);
        var rows = await providers.OrderBy(p => p.DisplayOrder).ThenBy(p => p.Name).ToListAsync(ct);
        return rows.Select(ToRecord).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<OidcProviderRecord?> GetAsync(Guid providerId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var provider = await Providers(lease.Context).FirstOrDefaultAsync(p => p.Id == providerId, ct);
        return provider is null ? null : ToRecord(provider);
    }

    /// <inheritdoc/>
    public virtual async Task<OidcProviderRecord?> FindByIssuerAsync(string issuerUrl, CancellationToken ct)
    {
        var normalized = issuerUrl.TrimEnd('/');
        await using var lease = await OpenAsync(ct);
        var provider = await Providers(lease.Context).FirstOrDefaultAsync(p =>
            p.IssuerUrl == normalized
            || p.IssuerUrl == normalized + "/"
            || p.IssuerUrl.TrimEnd('/') == normalized, ct);
        return provider is null ? null : ToRecord(provider);
    }

    /// <inheritdoc/>
    public virtual async Task<Guid> CreateAsync(NewOidcProvider provider, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var entity = new OidcProviderEntity
        {
            Id = provider.Id ?? Guid.CreateVersion7(),
            Name = provider.Name,
            ProviderType = provider.ProviderType,
            OAuth2SettingsJson = provider.OAuth2SettingsJson,
            IssuerUrl = provider.IssuerUrl,
            ClientId = provider.ClientId,
            ClientSecretEncrypted = provider.ClientSecretEncrypted,
            IsEnabled = provider.IsEnabled,
            DisplayOrder = provider.DisplayOrder,
            Icon = provider.Icon,
            ButtonColor = provider.ButtonColor,
        };
        if (provider.Scopes is not null)
            entity.Scopes = [.. provider.Scopes];
        if (provider.ClaimMappingsJson is not null)
            entity.ClaimMappingsJson = provider.ClaimMappingsJson;
        if (provider.DefaultRoles is not null)
            entity.DefaultRoles = [.. provider.DefaultRoles];

        lease.Context.OidcProviders.Add(entity);
        await SaveAsync(lease.Context, ct);
        return entity.Id;
    }

    /// <inheritdoc/>
    public virtual async Task<OidcProviderRecord?> UpdateAsync(Guid providerId, OidcProviderEdit edit, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var entity = await lease.Context.OidcProviders.FirstOrDefaultAsync(p => p.Id == providerId, ct);
        if (entity is null)
            return null;

        entity.Name = edit.Name;
        entity.ProviderType = edit.ProviderType;
        entity.OAuth2SettingsJson = edit.OAuth2SettingsJson;
        entity.IssuerUrl = edit.IssuerUrl;
        entity.ClientId = edit.ClientId;
        if (edit.ClientSecretEncrypted is not null)
            entity.ClientSecretEncrypted = edit.ClientSecretEncrypted;
        entity.Scopes = [.. edit.Scopes];
        entity.ClaimMappingsJson = edit.ClaimMappingsJson;
        entity.DefaultRoles = [.. edit.DefaultRoles];
        entity.IsEnabled = edit.IsEnabled;
        entity.DisplayOrder = edit.DisplayOrder;

        await SaveAsync(lease.Context, ct);
        return ToRecord(entity);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> DeleteAsync(Guid providerId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var entity = await lease.Context.OidcProviders.FirstOrDefaultAsync(p => p.Id == providerId, ct);
        if (entity is null)
            return false;

        lease.Context.OidcProviders.Remove(entity);
        await SaveAsync(lease.Context, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> SetEnabledAsync(Guid providerId, bool enabled, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var entity = await lease.Context.OidcProviders.FirstOrDefaultAsync(p => p.Id == providerId, ct);
        if (entity is null)
            return false;

        entity.IsEnabled = enabled;
        await SaveAsync(lease.Context, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> CacheDiscoveryDocumentAsync(Guid providerId, string documentJson, DateTime cachedAt, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var entity = await lease.Context.OidcProviders.FirstOrDefaultAsync(p => p.Id == providerId, ct);
        if (entity is null)
            return false;

        entity.DiscoveryDocumentJson = documentJson;
        entity.DiscoveryCachedAt = cachedAt;
        await SaveAsync(lease.Context, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task SyncConfiguredAsync(IReadOnlyList<NewOidcProvider> configured, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var configuredIds = configured.Select(p => p.Id ?? throw new ArgumentException(
            "A configured provider names its id.", nameof(configured))).ToList();
        var existing = await db.OidcProviders.ToListAsync(ct);

        foreach (var config in configured)
        {
            var entity = existing.FirstOrDefault(p => p.Id == config.Id);
            if (entity is null)
            {
                entity = new OidcProviderEntity { Id = config.Id!.Value };
                db.OidcProviders.Add(entity);
            }
            else
            {
                entity.UpdatedAt = now;
            }

            entity.Name = config.Name;
            entity.ProviderType = config.ProviderType;
            entity.OAuth2SettingsJson = config.OAuth2SettingsJson;
            entity.IssuerUrl = config.IssuerUrl;
            entity.ClientId = config.ClientId;
            entity.IsEnabled = config.IsEnabled;
            entity.DisplayOrder = config.DisplayOrder;
            entity.Scopes = [.. config.Scopes ?? []];
            entity.DefaultRoles = [.. config.DefaultRoles ?? []];
            entity.Icon = config.Icon;
            entity.ButtonColor = config.ButtonColor;
        }

        foreach (var stale in existing.Where(p => !configuredIds.Contains(p.Id) && p.IsEnabled))
        {
            stale.IsEnabled = false;
        }

        await SaveAsync(db, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<DateTime?> LastModifiedAsync(CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.OidcProviders.AsNoTracking()
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => (DateTime?)p.UpdatedAt)
            .FirstOrDefaultAsync(ct);
    }

    private static OidcProviderRecord ToRecord(OidcProviderEntity p) => new(
        p.Id,
        p.Name,
        p.ProviderType,
        p.OAuth2SettingsJson,
        p.IssuerUrl,
        p.ClientId,
        p.ClientSecretEncrypted,
        p.DiscoveryDocumentJson,
        p.DiscoveryCachedAt,
        [.. p.Scopes],
        p.ClaimMappingsJson,
        [.. p.DefaultRoles],
        p.IsEnabled,
        p.DisplayOrder,
        p.Icon,
        p.ButtonColor,
        p.CreatedAt,
        p.UpdatedAt);
}
