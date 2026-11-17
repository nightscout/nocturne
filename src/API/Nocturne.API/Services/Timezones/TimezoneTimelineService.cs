using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Timezones;
using Nocturne.Core.Models.Timezones;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Timezones;

/// <summary>
/// Persists and reads the current tenant's timezone timeline, in HTTP request scopes and background
/// connector-sync scopes alike: every context it opens reads its tenant from the scope's
/// <see cref="ITenantAccessor"/>, which must be resolved.
/// </summary>
/// <seealso cref="ITimezoneTimelineService"/>
public class TimezoneTimelineService : ITimezoneTimelineService
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly TimeProvider _time;
    private readonly ILogger<TimezoneTimelineService> _logger;

    public TimezoneTimelineService(
        INocturneDbContextFactory contexts,
        ITenantAccessor tenantAccessor,
        TimeProvider timeProvider,
        ILogger<TimezoneTimelineService> logger)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _tenantAccessor = tenantAccessor ?? throw new ArgumentNullException(nameof(tenantAccessor));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private Guid TenantId =>
        _tenantAccessor.Context?.TenantId
        ?? throw new InvalidOperationException("No tenant context for timezone timeline access.");

    /// <inheritdoc />
    public async Task<IReadOnlyList<TimezoneTimelineEntry>> GetTimelineAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        _ = TenantId;
        var entities = await db.TimezoneTimeline
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
        await using var db = await _contexts.CreateAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(ianaTimezone))
            return false;

        var tenantId = TenantId;

        if (await db.TimezoneTimeline.AnyAsync(cancellationToken))
            return false;

        var origin = new TimezoneTimelineEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            // Cover all history before any later move/trip entry.
            EffectiveFrom = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Unspecified),
            Timezone = ianaTimezone,
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            UpdatedAt = _time.GetUtcNow().UtcDateTime,
        };

        db.TimezoneTimeline.Add(origin);
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Seeded timezone timeline origin {Timezone} for tenant {TenantId}", ianaTimezone, tenantId);
        return true;
    }

    /// <inheritdoc />
    public async Task<TimezoneTimelineEntry> UpsertAsync(TimezoneTimelineEntry entry, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        ArgumentNullException.ThrowIfNull(entry);
        var tenantId = TenantId;

        TimezoneTimelineEntity? entity = null;
        if (entry.Id != Guid.Empty)
            entity = await db.TimezoneTimeline.FirstOrDefaultAsync(e => e.Id == entry.Id, cancellationToken);

        if (entity is null)
        {
            entity = TimezoneTimelineMapper.ToEntity(entry, tenantId);
            db.TimezoneTimeline.Add(entity);
        }
        else
        {
            entity.EffectiveFrom = DateTime.SpecifyKind(entry.EffectiveFrom, DateTimeKind.Unspecified);
            entity.Timezone = entry.Timezone;
            entity.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        }

        await db.SaveChangesAsync(cancellationToken);
        return TimezoneTimelineMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        _ = TenantId;
        var entity = await db.TimezoneTimeline.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null)
            return false;

        db.TimezoneTimeline.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
