using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// Stores the tenant's alert settings row, and saves the manual Do Not Disturb change that comes
/// with it on the same unit of work, so the two commit together.
/// </summary>
public class TenantAlertSettingsRepository : ITenantAlertSettingsRepository
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of <see cref="TenantAlertSettingsRepository"/>.</summary>
    public TenantAlertSettingsRepository(INocturneDbContextFactory contexts, TimeProvider? time = null)
    {
        _contexts = contexts;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public virtual async Task<TenantAlertSettingsSnapshot> GetAsync(CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);

        var row = await db.TenantAlertSettings
            .AsNoTracking()
            .Select(s => new TenantAlertSettingsSnapshot(
                s.DndScheduleEnabled,
                s.DndScheduleStart,
                s.DndScheduleEnd))
            .FirstOrDefaultAsync(ct);

        return row ?? TenantAlertSettingsSnapshot.Empty;
    }

    /// <inheritdoc/>
    public virtual async Task<TenantAlertSettingsSnapshot> GetOrCreateAsync(CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);

        var entity = await db.TenantAlertSettings.FirstOrDefaultAsync(ct);
        if (entity is null)
        {
            entity = new TenantAlertSettingsEntity { TenantId = db.TenantId };
            db.TenantAlertSettings.Add(entity);
            await db.SaveChangesAsync(ct);
        }

        return new TenantAlertSettingsSnapshot(
            entity.DndScheduleEnabled, entity.DndScheduleStart, entity.DndScheduleEnd);
    }

    /// <inheritdoc/>
    public virtual async Task UpdateAsync(
        TenantAlertSettingsSnapshot settings, ManualDndToggle? manual, CancellationToken ct)
    {
        await using var unit = await DbUnitOfWork.OpenAsync(_contexts, ct);
        var db = unit.Context;

        var entity = await db.TenantAlertSettings.FirstOrDefaultAsync(ct);
        if (entity is null)
        {
            entity = new TenantAlertSettingsEntity { TenantId = db.TenantId };
            db.TenantAlertSettings.Add(entity);
        }

        entity.DndScheduleEnabled = settings.DndScheduleEnabled;
        entity.DndScheduleStart = settings.DndScheduleStart;
        entity.DndScheduleEnd = settings.DndScheduleEnd;
        entity.UpdatedAt = _time.GetUtcNow().UtcDateTime;

        if (manual is { } toggle)
        {
            var windows = new DndWindowRepository(unit, _time);
            if (toggle.On)
                await windows.EnsureManualAsync(toggle.EndsAt, ct);
            else
                await windows.ClearManualAsync(ct);
        }

        await unit.CommitAsync(token => db.SaveChangesAsync(token), ct);
    }
}
