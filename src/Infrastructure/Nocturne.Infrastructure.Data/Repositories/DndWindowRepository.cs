using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// Stores Do Not Disturb windows on its own context per call, saving each, or on the caller's
/// <see cref="DbUnitOfWork"/> when constructed over one.
/// </summary>
/// <remarks>
/// <para>
/// The supersede rules differ on purpose. <see cref="CreateAsync"/> reconciles against the span of
/// the incoming window, because a device authors windows for spans of its own, offline included.
/// <see cref="EnsureManualAsync"/> reconciles against what is active now, because the manual toggle
/// has no span and keeps the running window so a repeat toggle does not reset its start.
/// <see cref="ClearManualAsync"/> clears every uncleared all-scope window and not only the
/// overlapping ones, since "off" is a statement about the whole scope.
/// </para>
/// <para>
/// "Active" is decided by <see cref="DndWindowSnapshot.IsActiveAt"/> alone. The query only drops
/// the rows it can never accept, which keeps the read bounded, since a window that runs out is
/// never cleared.
/// </para>
/// </remarks>
public class DndWindowRepository : IDndWindowRepository
{
    /// <summary>The <c>cleared_by</c> stamped on a window cleared because another superseded it.</summary>
    public const string SupersededBy = "system:superseded";

    private const string ManualSource = "web";

    private readonly INocturneDbContextFactory? _contexts;
    private readonly DbUnitOfWork? _unit;
    private readonly TimeProvider _time;

    /// <summary>A repository whose calls each open their own context and save what they write.</summary>
    public DndWindowRepository(INocturneDbContextFactory contexts, TimeProvider? time = null)
    {
        _contexts = contexts;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// A repository whose calls run on <paramref name="unit"/>, which the caller owns. A write is
    /// staged on the unit and the owner saves it, so <see cref="CreateAsync"/> cannot report
    /// <see cref="DndWindowCreateOutcome.IdInUse"/>: the insert fails when the owner saves.
    /// </summary>
    public DndWindowRepository(DbUnitOfWork unit, TimeProvider? time = null)
    {
        _unit = unit;
        _time = time ?? TimeProvider.System;
    }

    private ValueTask<DbContextLease> OpenAsync(CancellationToken ct) => DbContextLease.OpenAsync(_unit, _contexts, ct);

    private Task SaveAsync(NocturneDbContext db, CancellationToken ct) =>
        _unit is null ? db.SaveChangesAsync(ct) : Task.CompletedTask;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<DndWindowRecord>> GetActiveAsync(DndScope? scope, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var now = Now;

        var rows = await CouldBeActive(lease.Context.DndWindows.AsNoTracking(), scope, now)
            .OrderBy(w => w.StartedAt)
            .ToListAsync(ct);

        return rows.Where(w => IsActive(w, now)).Select(ToRecord).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<DndWindowSnapshot>> GetUnexpiredSnapshotsAsync(
        DateTime nowUtc, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);

        var rows = await CouldBeActive(lease.Context.DndWindows.AsNoTracking(), scope: null, nowUtc)
            .ToListAsync(ct);

        return rows.Select(ToSnapshot).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<DndWindowSnapshot>> GetReceivedByAsync(
        DateTime asOfReceiptUtc, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);

        var rows = await lease.Context.DndWindows
            .AsNoTracking()
            .Where(w => w.CreatedAt <= asOfReceiptUtc)
            .ToListAsync(ct);

        return rows.Select(ToSnapshot).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<DndWindowRecord>> GetOverlappingAsync(
        DateTime from, DateTime to, int limit, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);

        var rows = await lease.Context.DndWindows.AsNoTracking()
            .Where(w => w.StartedAt < to && (w.EndsAt == null || w.EndsAt > from) && (w.ClearedAt == null || w.ClearedAt > from))
            .OrderBy(w => w.StartedAt).Take(limit)
            .ToListAsync(ct);

        return rows.Select(ToRecord).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<bool> AnyOverlappingAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);

        return await lease.Context.DndWindows.AsNoTracking()
            .AnyAsync(w => w.StartedAt < to && (w.EndsAt == null || w.EndsAt > from) && (w.ClearedAt == null || w.ClearedAt > from), ct);
    }

    /// <inheritdoc/>
    public virtual async Task<DndWindowCreateResult> CreateAsync(NewDndWindow window, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;

        var existing = await db.DndWindows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == window.Id, ct);
        if (existing is not null)
            return new DndWindowCreateResult(DndWindowCreateOutcome.Existing, ToRecord(existing));

        SupersedeOverlapping(await OverlappingAsync(db, window, ct), window.StartedAt, Now);

        var entity = new DndWindowEntity
        {
            Id = window.Id,
            TenantId = db.TenantId,
            Scope = window.Scope,
            StartedAt = window.StartedAt,
            EndsAt = window.EndsAt,
            Source = window.Source,
        };
        db.DndWindows.Add(entity);

        try
        {
            // Lifted so a clamped member can record a window that began before the window of the clamp: truncating
            // an earlier window to end there leaves that row outside what the clamp shows, which PostgreSQL refuses
            // (42501) for an update checked against the SELECT policies. The save inserts this window and updates
            // only the overlapping windows read above under the clamp.
            await db.WriteWithHistoryClampLiftedAsync(async token =>
            {
                await SaveAsync(db, token);
                return true;
            }, ct);
        }
        catch (DbUpdateException ex) when (_unit is null && ex.InnerException is not System.Data.Common.DbException { SqlState: Npgsql.PostgresErrorCodes.InsufficientPrivilege })
        {
            // dnd_windows.id is the global key, but the lookup above runs under the tenant filter,
            // so an id another tenant owns is invisible there and the insert hits the key. A
            // concurrent retry by this tenant resolves to its stored window; otherwise the id can
            // never be stored.
            var raced = await db.DndWindows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == window.Id, ct);
            return raced is null
                ? new DndWindowCreateResult(DndWindowCreateOutcome.IdInUse, null)
                : new DndWindowCreateResult(DndWindowCreateOutcome.Existing, ToRecord(raced));
        }

        return new DndWindowCreateResult(DndWindowCreateOutcome.Created, ToRecord(entity));
    }

    /// <inheritdoc/>
    public virtual async Task<DndWindowRecord?> ClearAsync(Guid id, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;

        var window = await db.DndWindows.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (window is null)
            return await ClearedBeforeTheClampAsync(db, id, ct);

        if (window.ClearedAt is null)
        {
            window.ClearedAt = Now;
            await SaveAsync(db, ct);
        }

        return ToRecord(window);
    }

    /// <summary>
    /// A window the caller names that a history-clamped reader cannot see because it was cleared before the clamp's
    /// window, so a retried clear answers as the first one did; null when there is none, or it was not cleared.
    /// </summary>
    private static Task<DndWindowRecord?> ClearedBeforeTheClampAsync(NocturneDbContext db, Guid id, CancellationToken ct) =>
        !db.HistoryClamped
            ? Task.FromResult<DndWindowRecord?>(null)
            : db.WriteWithHistoryClampLiftedAsync(async token =>
                await db.DndWindows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id && w.ClearedAt != null, token) is { } cleared
                    ? ToRecord(cleared)
                    : null, ct);

    /// <inheritdoc/>
    public virtual async Task EnsureManualAsync(DateTime? endsAt, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var now = Now;

        var active = (await CouldBeActive(db.DndWindows, DndScope.All, now).ToListAsync(ct))
            .Where(w => IsActive(w, now))
            .OrderBy(w => w.StartedAt)
            .ToList();

        if (active.Count == 0)
        {
            db.DndWindows.Add(new DndWindowEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = db.TenantId,
                Scope = DndScope.All,
                StartedAt = now,
                EndsAt = endsAt,
                Source = ManualSource,
            });
        }
        else
        {
            active[0].EndsAt = endsAt;
            foreach (var extra in active.Skip(1))
            {
                extra.ClearedAt = now;
                extra.ClearedBy = SupersededBy;
            }
        }

        await SaveAsync(db, ct);
    }

    /// <inheritdoc/>
    public virtual async Task ClearManualAsync(CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var now = Now;

        var uncleared = await db.DndWindows
            .Where(w => w.Scope == DndScope.All && w.ClearedAt == null)
            .ToListAsync(ct);
        foreach (var window in uncleared)
            window.ClearedAt = now;

        await SaveAsync(db, ct);
    }

    /// <summary>
    /// Every uncleared window of <paramref name="scope"/> (any scope when null) that has not
    /// expired by <paramref name="now"/>: a superset of the active ones, which
    /// <see cref="IsActive"/> then decides.
    /// </summary>
    private static IQueryable<DndWindowEntity> CouldBeActive(
        IQueryable<DndWindowEntity> windows, DndScope? scope, DateTime now) =>
        windows.Where(w => (scope == null || w.Scope == scope) && w.ClearedAt == null)
            .NotEndedBy(w => w.EndsAt, now);

    internal static bool IsActive(DndWindowEntity w, DateTime now) =>
        new DndWindowSnapshot(w.Scope, w.StartedAt, w.EndsAt, w.ClearedAt, w.CreatedAt).IsActiveAt(now);

    private static Task<List<DndWindowEntity>> OverlappingAsync(
        NocturneDbContext db, NewDndWindow window, CancellationToken ct) =>
        CouldBeActive(db.DndWindows, window.Scope, window.StartedAt)
            .Where(w => window.EndsAt == null || w.StartedAt < window.EndsAt)
            .ToListAsync(ct);

    private static void SupersedeOverlapping(List<DndWindowEntity> overlapping, DateTime startedAt, DateTime now)
    {
        foreach (var w in overlapping)
        {
            if (w.StartedAt < startedAt)
            {
                w.EndsAt = startedAt;
            }
            else
            {
                w.ClearedAt = now;
                w.ClearedBy = SupersededBy;
            }
        }
    }

    // Npgsql can read a `timestamp` column back as Unspecified, which would compare wrongly
    // against a Utc `now` in the snapshot's naive comparison.
    private static DndWindowSnapshot ToSnapshot(DndWindowEntity w) =>
        new(w.Scope,
            DateTime.SpecifyKind(w.StartedAt, DateTimeKind.Utc),
            w.EndsAt is { } ends ? DateTime.SpecifyKind(ends, DateTimeKind.Utc) : null,
            w.ClearedAt is { } cleared ? DateTime.SpecifyKind(cleared, DateTimeKind.Utc) : null,
            DateTime.SpecifyKind(w.CreatedAt, DateTimeKind.Utc));

    private static DndWindowRecord ToRecord(DndWindowEntity w) =>
        new(w.Id, w.Scope, w.StartedAt, w.EndsAt, w.ClearedAt, w.ClearedBy, w.Source, w.CreatedAt);
}
