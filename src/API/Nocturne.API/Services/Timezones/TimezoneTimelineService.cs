using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Timezones;
using Nocturne.Core.Models.Timezones;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Services.Timezones;

/// <summary>
/// Persists and reads the current tenant's timezone timeline. Works in both HTTP request scopes and
/// background connector-sync scopes: it pins <see cref="NocturneDbContext.TenantId"/> from the ambient
/// <see cref="ITenantAccessor"/> on each call, so RLS (and the WITH CHECK on writes) scope correctly
/// without an HTTP context.
/// </summary>
/// <seealso cref="ITimezoneTimelineService"/>
public class TimezoneTimelineService : ITimezoneTimelineService
{
    private readonly NocturneDbContext _db;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ILogger<TimezoneTimelineService> _logger;

    public TimezoneTimelineService(
        NocturneDbContext db,
        ITenantAccessor tenantAccessor,
        ILogger<TimezoneTimelineService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tenantAccessor = tenantAccessor ?? throw new ArgumentNullException(nameof(tenantAccessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private Guid TenantId
    {
        get
        {
            var tenantId = _tenantAccessor.Context?.TenantId
                ?? throw new InvalidOperationException("No tenant context for timezone timeline access.");
            // Pin the context so RLS (USING + WITH CHECK) scopes to this tenant in any scope.
            _db.TenantId = tenantId;
            return tenantId;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TimezoneTimelineEntry>> GetTimelineAsync(CancellationToken cancellationToken = default)
    {
        _ = TenantId;
        var entities = await _db.TimezoneTimeline
            .OrderBy(e => e.EffectiveFrom)
            .ToListAsync(cancellationToken);

        return entities.Select(TimezoneTimelineMapper.ToDomainModel).ToList();
    }

    /// <inheritdoc />
    public async Task<TimezoneTimeline> GetResolverAsync(double? fallbackOffsetHours, CancellationToken cancellationToken = default)
    {
        var entries = await GetTimelineAsync(cancellationToken);
        return new TimezoneTimeline(entries, fallbackOffsetHours);
    }

    /// <inheritdoc />
    public async Task<bool> EnsureOriginAsync(string ianaTimezone, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ianaTimezone))
            return false;

        var tenantId = TenantId;

        if (await _db.TimezoneTimeline.AnyAsync(cancellationToken))
            return false;

        var origin = new TimezoneTimelineEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            // Cover all history before any later move/trip entry.
            EffectiveFrom = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Unspecified),
            Timezone = ianaTimezone,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.TimezoneTimeline.Add(origin);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Seeded timezone timeline origin {Timezone} for tenant {TenantId}", ianaTimezone, tenantId);
        return true;
    }

    /// <inheritdoc />
    public async Task<TimezoneTimelineEntry> UpsertAsync(TimezoneTimelineEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var tenantId = TenantId;

        TimezoneTimelineEntity? entity = null;
        if (entry.Id != Guid.Empty)
            entity = await _db.TimezoneTimeline.FirstOrDefaultAsync(e => e.Id == entry.Id, cancellationToken);

        if (entity is null)
        {
            entity = TimezoneTimelineMapper.ToEntity(entry, tenantId);
            _db.TimezoneTimeline.Add(entity);
        }
        else
        {
            entity.EffectiveFrom = DateTime.SpecifyKind(entry.EffectiveFrom, DateTimeKind.Unspecified);
            entity.Timezone = entry.Timezone;
            entity.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return TimezoneTimelineMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _ = TenantId;
        var entity = await _db.TimezoneTimeline.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null)
            return false;

        _db.TimezoneTimeline.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
