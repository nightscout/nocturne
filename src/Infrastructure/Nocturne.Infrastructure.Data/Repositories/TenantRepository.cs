using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="ITenantRepository"/> over <see cref="NocturneDbContext"/>: one context per call,
/// saving each write, or the caller's <see cref="DbUnitOfWork"/> when constructed over one.
/// </summary>
/// <remarks>
/// Registered transient, like the factory it holds: the tenant resolution middleware, the share
/// token and public access caches and the background sweeps resolve it from whatever provider they
/// hold. Writes are tracked reads and saves; the one exception, the share-access stamp, is a
/// single statement because it runs on every uncached share request.
/// </remarks>
public class TenantRepository : ITenantRepository
{
    private readonly INocturneDbContextFactory? _contexts;
    private readonly DbUnitOfWork? _unit;

    /// <summary>A repository whose calls each open their own context and save what they write.</summary>
    public TenantRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <summary>
    /// A repository whose calls run on <paramref name="unit"/>, which the caller owns. A write is
    /// staged on the unit and not saved: the owner saves it, so a provisioned tenant lands in one
    /// transaction with its roles, owner and public membership.
    /// </summary>
    public TenantRepository(DbUnitOfWork unit)
    {
        _unit = unit ?? throw new ArgumentNullException(nameof(unit));
    }

    private ValueTask<DbContextLease> OpenAsync(CancellationToken ct) => DbContextLease.OpenAsync(_unit, _contexts, ct);

    private Task SaveAsync(NocturneDbContext db, CancellationToken ct) =>
        _unit is null ? db.SaveChangesAsync(ct) : Task.CompletedTask;

    private IQueryable<TenantEntity> Tenants(NocturneDbContext db) =>
        (_unit is null ? db.Tenants.AsNoTracking() : db.Tenants).Include(t => t.DemoConfig);

    /// <inheritdoc/>
    public virtual async Task<Guid> CreateAsync(NewTenant tenant, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var entity = new TenantEntity
        {
            Id = tenant.Id ?? Guid.CreateVersion7(),
            Slug = tenant.Slug,
            DisplayName = tenant.DisplayName,
            IsActive = tenant.IsActive,
            IsDemo = tenant.IsDemo,
            AllowAccessRequests = tenant.AllowAccessRequests,
            AllowPublicDocs = tenant.AllowPublicDocs,
            OnboardingCompletedAt = tenant.OnboardingCompletedAt,
            ShareToken = tenant.ShareTokenHash,
            ShareTokenEncrypted = tenant.ShareTokenEncrypted,
            ShareTokenSetAt = tenant.ShareTokenSetAt,
        };
        lease.Context.Tenants.Add(entity);
        if (tenant.DemoConfig is { } demo)
        {
            lease.Context.TenantDemoConfigs.Add(new TenantDemoConfigEntity
            {
                TenantId = entity.Id,
                NextResetAt = demo.NextResetAt,
                LastResetAt = demo.LastResetAt,
                AccessMode = demo.AccessMode,
                BackfillDays = demo.BackfillDays,
                IntervalMinutes = demo.IntervalMinutes,
                ResetIntervalMinutes = demo.ResetIntervalMinutes,
            });
        }

        await SaveAsync(lease.Context, ct);
        return entity.Id;
    }

    /// <inheritdoc/>
    public virtual async Task<TenantRecord?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecord(await Tenants(lease.Context).FirstOrDefaultAsync(t => t.Id == id, ct));
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<TenantRecord>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];

        await using var lease = await OpenAsync(ct);
        var wanted = ids.ToList();
        return ToRecords(await Tenants(lease.Context).Where(t => wanted.Contains(t.Id)).ToListAsync(ct));
    }

    /// <inheritdoc/>
    public virtual async Task<TenantRecord?> FindBySlugAsync(string slug, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecord(await Tenants(lease.Context).FirstOrDefaultAsync(t => t.Slug == slug, ct));
    }

    /// <inheritdoc/>
    public virtual async Task<TenantRecord?> FindByShareTokenHashAsync(string tokenHash, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecord(await Tenants(lease.Context).FirstOrDefaultAsync(t => t.ShareToken == tokenHash, ct));
    }

    /// <inheritdoc/>
    public virtual async Task<bool> ShareTokenHashExistsAsync(string tokenHash, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.Tenants.AnyAsync(t => t.ShareToken == tokenHash, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> IsActiveAsync(Guid id, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.Tenants.AnyAsync(t => t.Id == id && t.IsActive, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> SlugExistsAsync(string slug, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.Tenants.AnyAsync(t => t.Slug == slug, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<TenantRecord?> FindSoleTenantAsync(CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var candidates = await Tenants(lease.Context).ExcludeDemo().Where(t => t.IsActive).Take(2).ToListAsync(ct);
        return candidates.Count == 1 ? ToRecord(candidates[0]) : null;
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<TenantRecord>> ListNonDemoAsync(int take, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecords(await Tenants(lease.Context).ExcludeDemo().OrderBy(t => t.SysCreatedAt).ThenBy(t => t.Id).Take(take).ToListAsync(ct));
    }

    /// <inheritdoc/>
    public virtual async Task<bool> AnyNonDemoAsync(CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.Tenants.ExcludeDemo().AnyAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<TenantRecord?> FindDemoTenantAsync(CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecord(await Tenants(lease.Context).FirstOrDefaultAsync(t => t.IsDemo, ct));
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<TenantRecord>> ListAsync(bool activeOnly, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var tenants = Tenants(lease.Context);
        if (activeOnly)
            tenants = tenants.Where(t => t.IsActive);
        return ToRecords(await tenants.OrderBy(t => t.SysCreatedAt).ThenBy(t => t.Id).ToListAsync(ct));
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<Guid>> ListIdsAsync(bool activeOnly, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        IQueryable<TenantEntity> tenants = lease.Context.Tenants.AsNoTracking();
        if (activeOnly)
            tenants = tenants.Where(t => t.IsActive);
        return await tenants.OrderBy(t => t.SysCreatedAt).ThenBy(t => t.Id).Select(t => t.Id).ToListAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> UpdateAsync(Guid id, TenantEdit edit, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var entity = await lease.Context.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (entity is null)
            return false;

        if (edit.DisplayName is not null)
            entity.DisplayName = edit.DisplayName;
        if (edit.IsActive is { } active)
            entity.IsActive = active;
        if (edit.IsDemo is { } demo)
            entity.IsDemo = demo;
        if (edit.AllowAccessRequests is { } allowRequests)
            entity.AllowAccessRequests = allowRequests;
        if (edit.AllowPublicDocs is { } allowDocs)
            entity.AllowPublicDocs = allowDocs;
        if (edit.PatientRelationship is { } relationship)
            entity.PatientRelationship = relationship;
        if (edit.HandedOverAt is { } handedOver)
            entity.HandedOverAt = handedOver;
        if (edit.DefaultGlucoseUnits is not null)
            entity.DefaultGlucoseUnits = edit.DefaultGlucoseUnits;
        if (edit.OnboardingCompletedAt is { } onboarded)
            entity.OnboardingCompletedAt = onboarded;
        if (edit.SetupHubEnrolledAt is { } enrolled)
            entity.SetupHubEnrolledAt = enrolled;
        if (edit.SetupStripDismissedRevision is not null)
            entity.SetupStripDismissedRevision = edit.SetupStripDismissedRevision;

        if (edit.DemoConfig is { } demoEdit)
        {
            var config = await lease.Context.TenantDemoConfigs.FirstOrDefaultAsync(c => c.TenantId == id, ct);
            if (config is null)
            {
                config = new TenantDemoConfigEntity { TenantId = id };
                lease.Context.TenantDemoConfigs.Add(config);
            }

            if (demoEdit.NextResetAt is { } next)
                config.NextResetAt = next;
            if (demoEdit.LastResetAt is { } last)
                config.LastResetAt = last;
        }

        await SaveAsync(lease.Context, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> SetShareTokenAsync(Guid id, ShareTokenState token, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var entity = await lease.Context.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (entity is null)
            return false;

        entity.ShareToken = token.Hash;
        entity.ShareTokenEncrypted = token.Encrypted;
        entity.ShareTokenSetAt = token.SetAt;
        await SaveAsync(lease.Context, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> RecordShareAccessAsync(Guid id, DateTime at, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.Tenants
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ShareLastAccessedAt, at), ct) > 0;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var entity = await lease.Context.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (entity is null)
            return false;

        lease.Context.Tenants.Remove(entity);
        await SaveAsync(lease.Context, ct);
        return true;
    }

    private static IReadOnlyList<TenantRecord> ToRecords(List<TenantEntity> tenants) =>
        tenants.Select(t => ToRecord(t)!).ToList();

    internal static TenantRecord? ToRecord(TenantEntity? tenant) =>
        tenant is null ? null : new TenantRecord(
            tenant.Id,
            tenant.Slug,
            tenant.DisplayName,
            tenant.IsActive,
            tenant.IsDemo,
            tenant.AllowAccessRequests,
            tenant.AllowPublicDocs,
            tenant.OnboardingCompletedAt,
            tenant.SetupHubEnrolledAt,
            tenant.SetupStripDismissedRevision,
            tenant.PatientRelationship,
            tenant.HandedOverAt,
            tenant.DefaultGlucoseUnits,
            tenant.LastReadingAt,
            tenant.ShareToken,
            tenant.ShareTokenEncrypted,
            tenant.ShareTokenSetAt,
            tenant.ShareLastAccessedAt,
            tenant.SysCreatedAt,
            tenant.SysUpdatedAt,
            tenant.DemoConfig is null ? null : new DemoTenantConfig(
                tenant.DemoConfig.NextResetAt,
                tenant.DemoConfig.LastResetAt,
                tenant.DemoConfig.AccessMode,
                tenant.DemoConfig.BackfillDays,
                tenant.DemoConfig.IntervalMinutes,
                tenant.DemoConfig.ResetIntervalMinutes));
}
