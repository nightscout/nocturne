using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// Repository for alert tracker state and excursion persistence.
/// Methods are virtual to allow mocking with CallBase in tests.
/// </summary>
public class AlertTrackerRepository : IAlertTrackerRepository
{
    private readonly NocturneDbContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="AlertTrackerRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    public AlertTrackerRepository(NocturneDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Get the tracker state for a specific alert rule.
    /// </summary>
    /// <param name="alertRuleId">The unique identifier of the alert rule.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The tracker state, or null if not found.</returns>
    public virtual async Task<AlertTrackerState?> GetTrackerStateAsync(
        Guid alertRuleId,
        CancellationToken ct = default)
    {
        var entity = await _context.AlertTrackerState
            .FirstOrDefaultAsync(s => s.AlertRuleId == alertRuleId, ct);

        return entity == null ? null : MapTrackerState(entity);
    }

    /// <summary>
    /// Insert or update the tracker state for a rule.
    /// </summary>
    /// <param name="state">The tracker state to upsert.</param>
    /// <param name="ct">The cancellation token.</param>
    public virtual async Task UpsertTrackerStateAsync(
        AlertTrackerState state,
        CancellationToken ct = default)
    {
        var existing = await _context.AlertTrackerState
            .FirstOrDefaultAsync(s => s.AlertRuleId == state.AlertRuleId, ct);

        if (existing == null)
        {
            _context.AlertTrackerState.Add(new AlertTrackerStateEntity
            {
                AlertRuleId = state.AlertRuleId,
                State = state.State,
                ConfirmationCount = state.ConfirmationCount,
                ActiveExcursionId = state.ActiveExcursionId,
                UpdatedAt = state.UpdatedAt,
                HysteresisStartedAt = state.HysteresisStartedAt,
                AwaitingRearm = state.AwaitingRearm,
            });
        }
        else
        {
            existing.State = state.State;
            existing.ConfirmationCount = state.ConfirmationCount;
            existing.ActiveExcursionId = state.ActiveExcursionId;
            existing.UpdatedAt = state.UpdatedAt;
            existing.HysteresisStartedAt = state.HysteresisStartedAt;
            existing.AwaitingRearm = state.AwaitingRearm;
        }

        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Get the alert rule configuration.
    /// </summary>
    /// <param name="alertRuleId">The unique identifier of the alert rule.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The alert rule, or null if not found.</returns>
    public virtual async Task<AlertRule?> GetRuleAsync(
        Guid alertRuleId,
        CancellationToken ct = default)
    {
        var entity = await _context.AlertRules
            .FirstOrDefaultAsync(r => r.Id == alertRuleId, ct);

        return entity == null ? null : MapAlertRule(entity);
    }

    /// <inheritdoc/>
    public virtual async Task<AlertExcursion?> GetExcursionAsync(Guid excursionId, CancellationToken ct = default)
    {
        var entity = await _context.AlertExcursions
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == excursionId, ct);
        return entity is null ? null : MapAlertExcursion(entity);
    }

    /// <summary>
    /// Create a new excursion record and return it.
    /// </summary>
    /// <param name="alertRuleId">The unique identifier of the alert rule.</param>
    /// <param name="startedAt">The timestamp when the excursion started.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created alert excursion.</returns>
    public virtual async Task<AlertExcursion> CreateExcursionAsync(
        Guid alertRuleId,
        DateTime startedAt,
        CancellationToken ct = default)
    {
        var excursion = new AlertExcursionEntity
        {
            Id = Guid.CreateVersion7(),
            AlertRuleId = alertRuleId,
            StartedAt = startedAt,
        };

        _context.AlertExcursions.Add(excursion);
        await _context.SaveChangesAsync(ct);
        return MapAlertExcursion(excursion);
    }

    /// <summary>
    /// Close an excursion by setting its EndedAt timestamp.
    /// </summary>
    /// <param name="excursionId">The unique identifier of the excursion.</param>
    /// <param name="endedAt">The timestamp when the excursion ended.</param>
    /// <param name="ct">The cancellation token.</param>
    public virtual async Task CloseExcursionAsync(
        Guid excursionId,
        DateTime endedAt,
        CancellationToken ct = default)
    {
        var excursion = await _context.AlertExcursions
            .FirstOrDefaultAsync(e => e.Id == excursionId, ct);

        if (excursion != null)
        {
            excursion.EndedAt = endedAt;
            await _context.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Record the start of hysteresis on an excursion.
    /// </summary>
    /// <param name="excursionId">The unique identifier of the excursion.</param>
    /// <param name="hysteresisStartedAt">The timestamp when hysteresis started.</param>
    /// <param name="ct">The cancellation token.</param>
    public virtual async Task SetHysteresisStartedAsync(
        Guid excursionId,
        DateTime hysteresisStartedAt,
        CancellationToken ct = default)
    {
        var excursion = await _context.AlertExcursions
            .FirstOrDefaultAsync(e => e.Id == excursionId, ct);

        if (excursion != null)
        {
            excursion.HysteresisStartedAt = hysteresisStartedAt;
            await _context.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Clear the hysteresis timestamp on an excursion (when resuming from hysteresis).
    /// </summary>
    /// <param name="excursionId">The unique identifier of the excursion.</param>
    /// <param name="ct">The cancellation token.</param>
    public virtual async Task ClearHysteresisAsync(
        Guid excursionId,
        CancellationToken ct = default)
    {
        var excursion = await _context.AlertExcursions
            .FirstOrDefaultAsync(e => e.Id == excursionId, ct);

        if (excursion != null)
        {
            excursion.HysteresisStartedAt = null;
            await _context.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// First key of the two-key advisory lock form, naming the alert rule transition lock. The
    /// second is <see cref="LockKey"/>.
    /// </summary>
    private const int TransitionLockClass = 0x4E41_5254;

    /// <inheritdoc/>
    /// <remarks>
    /// A PostgreSQL transaction-scoped advisory lock, so replicas sharing the database serialise
    /// on a rule and the lock goes with the commit or rollback. Other providers have no other
    /// process to exclude and take nothing. Two rules whose keys collide only wait for each other.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No transaction is open on the context.</exception>
    public virtual async Task LockRuleAsync(Guid alertRuleId, CancellationToken ct = default)
    {
        if (!_context.Database.IsNpgsql())
            return;
        if (_context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A rule's transition lock is taken inside a transaction");

        await _context.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({TransitionLockClass}, {LockKey(alertRuleId)})", ct);
    }

    private static int LockKey(Guid alertRuleId)
    {
        Span<byte> bytes = stackalloc byte[16];
        alertRuleId.TryWriteBytes(bytes);
        return BitConverter.ToInt32(bytes[..4]) ^ BitConverter.ToInt32(bytes[4..8])
            ^ BitConverter.ToInt32(bytes[8..12]) ^ BitConverter.ToInt32(bytes[12..]);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Runs under <see cref="RetryingTransactionExtensions.ExecuteInTransactionAsync{T}"/>. Opening
    /// the connection sets the tenant GUCs (TenantConnectionInterceptor), so every statement in the
    /// transaction runs under the context's tenant. Tracker state and excursions are detached before
    /// each attempt even when tracked before the call, so the work reads them fresh under the rule's
    /// lock.
    /// </remarks>
    public virtual Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> work,
        Func<T, CancellationToken, Task<bool>>? verifySucceeded = null,
        CancellationToken ct = default) =>
        _context.ExecuteInTransactionAsync(
            work,
            verifySucceeded,
            entity => entity is AlertTrackerStateEntity or AlertExcursionEntity,
            ct);

    private static AlertTrackerState MapTrackerState(AlertTrackerStateEntity entity) => new()
    {
        AlertRuleId = entity.AlertRuleId,
        State = entity.State,
        ConfirmationCount = entity.ConfirmationCount,
        ActiveExcursionId = entity.ActiveExcursionId,
        UpdatedAt = entity.UpdatedAt,
        HysteresisStartedAt = entity.HysteresisStartedAt,
        AwaitingRearm = entity.AwaitingRearm,
    };

    private static AlertRule MapAlertRule(AlertRuleEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        Description = entity.Description,
        ConditionType = entity.ConditionType,
        ConditionParams = entity.ConditionParams,
        Severity = entity.Severity,
        ClientConfiguration = entity.ClientConfiguration,
        IsEnabled = entity.IsEnabled,
        SortOrder = entity.SortOrder,
        AutoResolveEnabled = entity.AutoResolveEnabled,
        AutoResolveParams = entity.AutoResolveParams,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
    };

    private static AlertExcursion MapAlertExcursion(AlertExcursionEntity entity) => new()
    {
        Id = entity.Id,
        AlertRuleId = entity.AlertRuleId,
        StartedAt = entity.StartedAt,
        EndedAt = entity.EndedAt,
        AcknowledgedAt = entity.AcknowledgedAt,
        AcknowledgedBy = entity.AcknowledgedBy,
        HysteresisStartedAt = entity.HysteresisStartedAt,
    };
}
