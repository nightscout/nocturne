using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// Repository for alert tracker state and excursion persistence. Each call runs on its own context,
/// or on the caller's <see cref="DbUnitOfWork"/> when constructed over one.
/// Methods are virtual to allow mocking with CallBase in tests.
/// </summary>
public class AlertTrackerRepository : IAlertTrackerRepository
{
    private readonly INocturneDbContextFactory? _contexts;
    private readonly DbUnitOfWork? _unit;

    /// <summary>A repository whose calls each open their own context.</summary>
    public AlertTrackerRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts;
        Timers = new ConditionTimerRepository(contexts);
    }

    /// <summary>A repository whose calls run on <paramref name="unit"/>, which the caller owns.</summary>
    public AlertTrackerRepository(DbUnitOfWork unit)
    {
        _unit = unit;
        Timers = new ConditionTimerRepository(unit);
    }

    private ValueTask<DbContextLease> OpenAsync(CancellationToken ct) => DbContextLease.OpenAsync(_unit, _contexts, ct);

    /// <inheritdoc/>
    public IConditionTimerStore Timers { get; }

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
        await using var lease = await OpenAsync(ct);
        var entity = await lease.Context.AlertTrackerState
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
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var existing = await db.AlertTrackerState
            .FirstOrDefaultAsync(s => s.AlertRuleId == state.AlertRuleId, ct);

        if (existing == null)
        {
            db.AlertTrackerState.Add(new AlertTrackerStateEntity
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

        await db.SaveChangesAsync(ct);
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
        await using var lease = await OpenAsync(ct);
        var entity = await lease.Context.AlertRules
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == alertRuleId, ct);

        return entity == null ? null : MapAlertRule(entity);
    }

    /// <inheritdoc/>
    public virtual async Task<AlertExcursion?> GetExcursionAsync(Guid excursionId, CancellationToken ct = default)
    {
        await using var lease = await OpenAsync(ct);
        var entity = await lease.Context.AlertExcursions
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

        await using var lease = await OpenAsync(ct);
        lease.Context.AlertExcursions.Add(excursion);
        await lease.Context.SaveChangesAsync(ct);
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
        await using var lease = await OpenAsync(ct);
        var excursion = await lease.Context.AlertExcursions
            .FirstOrDefaultAsync(e => e.Id == excursionId, ct);

        if (excursion != null)
        {
            excursion.EndedAt = endedAt;
            await lease.Context.SaveChangesAsync(ct);
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
        await using var lease = await OpenAsync(ct);
        var excursion = await lease.Context.AlertExcursions
            .FirstOrDefaultAsync(e => e.Id == excursionId, ct);

        if (excursion != null)
        {
            excursion.HysteresisStartedAt = hysteresisStartedAt;
            await lease.Context.SaveChangesAsync(ct);
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
        await using var lease = await OpenAsync(ct);
        var excursion = await lease.Context.AlertExcursions
            .FirstOrDefaultAsync(e => e.Id == excursionId, ct);

        if (excursion != null)
        {
            excursion.HysteresisStartedAt = null;
            await lease.Context.SaveChangesAsync(ct);
        }
    }

    private const int TransitionLockClass = 0x4E41_5254;

    /// <inheritdoc/>
    /// <remarks>See <see cref="AdvisoryLockExtensions"/>.</remarks>
    /// <exception cref="InvalidOperationException">
    /// The repository is not the one <see cref="ExecuteInTransactionAsync{T}"/> handed its work, or
    /// no transaction is open on its unit.
    /// </exception>
    public virtual Task LockRuleAsync(Guid alertRuleId, CancellationToken ct = default) =>
        _unit is null
            ? throw new InvalidOperationException("An advisory lock is taken inside a transaction")
            : _unit.Context.LockForTransactionAsync(TransitionLockClass, alertRuleId, ct);

    /// <inheritdoc/>
    /// <remarks>
    /// Runs as a <see cref="DbUnitOfWork"/>: this repository's own when it was constructed over one,
    /// else a new unit, and hands the work a repository over it. Opening the connection sets the
    /// tenant GUCs (TenantConnectionInterceptor), so every statement in the transaction runs under the
    /// context's tenant. Tracker state, excursions and timers are detached before each attempt even
    /// when tracked before the call, so the work reads them fresh under the rule's lock.
    /// </remarks>
    public virtual async Task<T> ExecuteInTransactionAsync<T>(
        Func<IAlertTrackerRepository, CancellationToken, Task<T>> work,
        Func<IAlertTrackerRepository, T, CancellationToken, Task<bool>>? verifySucceeded = null,
        CancellationToken ct = default)
    {
        if (_unit is not null)
            return await CommitAsync(_unit, this, work, verifySucceeded, ct);

        await using var unit = await DbUnitOfWork.OpenAsync(_contexts!, ct);
        return await CommitAsync(unit, new AlertTrackerRepository(unit), work, verifySucceeded, ct);
    }

    private static Task<T> CommitAsync<T>(
        DbUnitOfWork unit,
        IAlertTrackerRepository bound,
        Func<IAlertTrackerRepository, CancellationToken, Task<T>> work,
        Func<IAlertTrackerRepository, T, CancellationToken, Task<bool>>? verifySucceeded,
        CancellationToken ct) =>
        unit.CommitAsync(
            token => work(bound, token),
            verifySucceeded is null ? null : (result, token) => verifySucceeded(bound, result, token),
            entity => entity is AlertTrackerStateEntity or AlertExcursionEntity or AlertConditionTimerEntity,
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
