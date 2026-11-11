using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="IOAuthClientRepository"/> over <see cref="NocturneDbContext"/>: one context per call,
/// saving each write, or the caller's <see cref="DbUnitOfWork"/> when constructed over one. The
/// redirect URIs are stored as a JSON array.
/// </summary>
/// <remarks>
/// Registered transient, like the factory it holds: the hosted cleanup sweep is a singleton, and
/// resolving the repository from the root gives it the root factory, which reads as no one and is
/// pinned per tenant on the sweep's call.
/// </remarks>
public class OAuthClientRepository : IOAuthClientRepository
{
    private const int SoftwareClientLockClass = 0x4E53_434C;

    private readonly INocturneDbContextFactory? _contexts;
    private readonly DbUnitOfWork? _unit;

    /// <summary>A repository whose calls each open their own context and save what they write.</summary>
    public OAuthClientRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <summary>
    /// A repository whose calls run on <paramref name="unit"/>, which the caller owns. A write is
    /// staged on the unit and not saved: the owner saves it.
    /// </summary>
    public OAuthClientRepository(DbUnitOfWork unit)
    {
        _unit = unit ?? throw new ArgumentNullException(nameof(unit));
    }

    private ValueTask<DbContextLease> OpenAsync(CancellationToken ct) => DbContextLease.OpenAsync(_unit, _contexts, ct);

    private async ValueTask<NocturneDbContext> OpenForTenantAsync(Guid tenantId, CancellationToken ct) =>
        await (_contexts ?? throw new InvalidOperationException(
                "A repository over a unit of work runs on the unit's tenant and cannot pin another."))
            .CreateForTenantAsync(tenantId, ct);

    private IQueryable<OAuthClientEntity> Clients(NocturneDbContext db) =>
        _unit is null ? db.OAuthClients.AsNoTracking() : db.OAuthClients;

    /// <inheritdoc/>
    public virtual async Task<OAuthClientRecord?> FindByClientIdAsync(string clientId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecord(await Clients(lease.Context).FirstOrDefaultAsync(c => c.ClientId == clientId, ct));
    }

    /// <inheritdoc/>
    public virtual async Task<OAuthClientRecord?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecord(await Clients(lease.Context).FirstOrDefaultAsync(c => c.Id == id, ct));
    }

    /// <inheritdoc/>
    public virtual async Task<OAuthClientRecord> CreateAsync(NewOAuthClient client, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entity = ToEntity(client, client.RedirectUris);
        db.OAuthClients.Add(entity);
        if (_unit is null)
            await db.SaveChangesAsync(ct);
        return ToRecord(entity)!;
    }

    /// <inheritdoc/>
    public virtual async Task<OAuthClientRedirectUriRegistration> FindOrCreateWithRedirectUriAsync(
        Guid tenantId, NewOAuthClient client, string redirectUri, int maxRedirectUris, CancellationToken ct)
    {
        if (client.SoftwareId is not { } softwareId)
            throw new ArgumentException("A client found by software id needs one.", nameof(client));

        await using var db = await OpenForTenantAsync(tenantId, ct);
        return await db.ExecuteInTransactionAsync(async token =>
        {
            await db.LockForTransactionAsync(SoftwareClientLockClass, tenantId, token);

            var entity = await db.OAuthClients
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.SoftwareId == softwareId, token);

            if (entity is null)
            {
                entity = ToEntity(client, [redirectUri]);
                entity.TenantId = tenantId;
                db.OAuthClients.Add(entity);
                await db.SaveChangesAsync(token);
                return new OAuthClientRedirectUriRegistration(entity.ClientId, true, 1);
            }

            var registered = DeserializeRedirectUris(entity.RedirectUris);
            if (registered.Contains(redirectUri, StringComparer.Ordinal))
                return new OAuthClientRedirectUriRegistration(entity.ClientId, true, registered.Count);

            if (registered.Count >= maxRedirectUris)
                return new OAuthClientRedirectUriRegistration(entity.ClientId, false, registered.Count);

            registered.Add(redirectUri);
            entity.RedirectUris = JsonSerializer.Serialize(registered);
            await db.SaveChangesAsync(token);
            return new OAuthClientRedirectUriRegistration(entity.ClientId, true, registered.Count);
        }, ct: ct);
    }

    /// <inheritdoc/>
    public virtual async Task<int> DeleteUnreferencedAsync(
        Guid tenantId, DateTime createdBefore, string keptSoftwareId, CancellationToken ct)
    {
        await using var db = await OpenForTenantAsync(tenantId, ct);
        return await db.OAuthClients
            .Where(c => c.CreatedAt < createdBefore
                        && c.SoftwareId != keptSoftwareId
                        && !db.OAuthGrants.Any(g => g.ClientEntityId == c.Id)
                        && !db.OAuthAuthorizationCodes.Any(a => a.ClientEntityId == c.Id)
                        && !db.OAuthDeviceCodes.Any(d => d.ClientId == c.ClientId))
            .ExecuteDeleteAsync(ct);
    }

    private static OAuthClientEntity ToEntity(NewOAuthClient client, IReadOnlyList<string> redirectUris) => new()
    {
        Id = Guid.CreateVersion7(),
        ClientId = client.ClientId,
        SoftwareId = client.SoftwareId,
        ClientName = client.ClientName,
        ClientUri = client.ClientUri,
        LogoUri = client.LogoUri,
        DisplayName = client.DisplayName,
        IsKnown = client.IsKnown,
        RedirectUris = JsonSerializer.Serialize(redirectUris),
        CreatedFromIp = client.CreatedFromIp,
    };

    private static List<string> DeserializeRedirectUris(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static OAuthClientRecord? ToRecord(OAuthClientEntity? client) =>
        client is null ? null : new OAuthClientRecord(
            client.Id,
            client.TenantId,
            client.ClientId,
            client.SoftwareId,
            client.ClientName,
            client.ClientUri,
            client.LogoUri,
            client.CreatedFromIp,
            client.DisplayName,
            client.IsKnown,
            DeserializeRedirectUris(client.RedirectUris),
            client.CreatedAt,
            client.UpdatedAt);
}
