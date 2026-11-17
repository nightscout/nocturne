using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Interceptors;

/// <summary>
/// Drops a backend's auto-prepared statements after a command on it fails, before the next command
/// runs on that backend. A workaround for npgsql/npgsql#6391: remove it once a release carrying the
/// fix ships and the repo moves to it.
/// </summary>
/// <remarks>
/// Npgsql marks a statement it is auto-preparing as prepared once PostgreSQL has parsed it, but
/// records its row description only from the Describe that follows Bind. An error raised at Bind (a
/// value too long for its column or out of range, folded into the plan) arrives between the two, so
/// the statement stays prepared with no description. The next run of it on that physical connection,
/// whichever lessee the pool hands it to, reads its first DataRow as a protocol violation ("Received
/// unexpected backend message DataRow") and breaks the connection. Which failures arrive at Bind
/// depends on the plan, so every failure on a connection still open marks its backend: a server
/// error, a cancellation or a timeout alike.
/// <para>
/// The mark is the backend's process id rather than the lease, because the repair cannot always run
/// in the lease that failed: <c>DEALLOCATE</c> is refused in an aborted transaction (25P02), and the
/// connection may go back to the pool before anything else runs on it. In an aborted transaction the
/// mark stays for the backend's next command; the command it precedes fails on the aborted
/// transaction either way.
/// </para>
/// <para>
/// A batched <c>SaveChanges</c> reports a statement failing after the batch's first result set to
/// no command hook, and has closed its connection by the time it reports the failure, so each
/// context's backend is recorded as its commands execute and marked from
/// <see cref="SaveChangesFailed"/>. Commands sent through raw ADO.NET on a context's connection
/// (<c>TenantConnectionInterceptor</c>'s session setup, <c>ApsSnapshotCurvesRewriter</c>,
/// <c>TempBasalOriginCorroborator</c>) pass through no interceptor, so their failures mark nothing.
/// </para>
/// </remarks>
public sealed class PreparedStatementRepairInterceptor : DbCommandInterceptor, ISaveChangesInterceptor
{
    private readonly ConcurrentDictionary<int, byte> _unsettledBackends = new();
    private readonly ConditionalWeakTable<DbContext, StrongBox<int>> _backendOfContext = new();

    /// <inheritdoc />
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => Mark(command);

    /// <inheritdoc />
    public override Task CommandFailedAsync(
        DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Mark(command);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData) => Mark(command);

    /// <inheritdoc />
    public override Task CommandCanceledAsync(
        DbCommand command, CommandEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Mark(command);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Executing(command, eventData);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Executing(command, eventData);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Executing(command, eventData);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Executing(command, eventData);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Executing(command, eventData);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Executing(command, eventData);
        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// Forgets the backend an earlier operation on the context used, so a save that fails before
    /// sending a command marks none.
    /// </summary>
    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Forget(eventData.Context);
        return result;
    }

    /// <inheritdoc cref="SavingChanges" />
    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Forget(eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public void SaveChangesFailed(DbContextErrorEventData eventData) => MarkBackendOf(eventData.Context);

    /// <inheritdoc />
    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        MarkBackendOf(eventData.Context);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void SaveChangesCanceled(DbContextEventData eventData) => MarkBackendOf(eventData.Context);

    /// <inheritdoc />
    public Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        MarkBackendOf(eventData.Context);
        return Task.CompletedTask;
    }

    private void Mark(DbCommand command)
    {
        if (command.Connection is NpgsqlConnection { State: ConnectionState.Open } connection)
            _unsettledBackends.TryAdd(connection.ProcessID, 0);
    }

    private void MarkBackendOf(DbContext? context)
    {
        if (context is not null && _backendOfContext.TryGetValue(context, out var backend))
            _unsettledBackends.TryAdd(backend.Value, 0);
    }

    private void Forget(DbContext? context)
    {
        if (context is not null)
            _backendOfContext.Remove(context);
    }

    private void Executing(DbCommand command, CommandEventData eventData)
    {
        if (command.Connection is not NpgsqlConnection { State: ConnectionState.Open } connection)
            return;

        var backend = connection.ProcessID;
        if (eventData.Context is { } context)
            _backendOfContext.GetValue(context, static _ => new StrongBox<int>()).Value = backend;

        Repair(connection, backend);
    }

    /// <remarks>
    /// <see cref="NpgsqlConnection.UnprepareAll"/> has no asynchronous form; it is one round trip,
    /// paid only after a failure.
    /// </remarks>
    private void Repair(NpgsqlConnection connection, int backend)
    {
        if (!_unsettledBackends.ContainsKey(backend))
            return;

        try
        {
            connection.UnprepareAll();
            _unsettledBackends.TryRemove(backend, out _);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InFailedSqlTransaction)
        {
        }
    }
}
