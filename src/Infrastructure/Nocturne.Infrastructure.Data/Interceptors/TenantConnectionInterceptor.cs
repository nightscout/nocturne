using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Interceptors;

/// <summary>
/// EF Core connection interceptor that writes the PostgreSQL session state Row Level Security reads
/// on every open of a <see cref="NocturneDbContext"/>'s connection: <c>app.current_tenant_id</c>,
/// <c>app.current_subject_id</c>, the share and history-clamp GUCs, and <c>application_name</c>.
/// </summary>
/// <remarks>
/// <para>
/// The context pool skips Npgsql's session reset on pool return
/// (<see cref="Configuration.PostgresRuntimeOptions.BuildRuntimeDataSource"/>), so a connection reaches
/// its next lessee still holding the previous one's session. Every value is therefore written on every
/// open, in one statement, and an absent tenant or subject as the empty string, which every policy
/// reads as unset through <c>NULLIF(current_setting(...), '')</c>: an unpinned context matches no
/// tenant's rows, and the <c>subject_pin</c> policy admits every row. Nothing is cleared on close. The
/// synchronous open is covered as well as the asynchronous one, since a context opened synchronously
/// would otherwise read the previous lessee's tenant. Nothing else may leave session state on a
/// pooled connection (<c>SessionStateGuardTests</c>).
/// </para>
/// <para>
/// A write that fails or is cancelled closes the connection before the exception leaves. EF does not
/// close a connection whose open threw, and its next open of a connection still open (the retry
/// strategy's, after a command timeout) skips this interceptor, so it would run on the previous
/// lessee's session.
/// </para>
/// <para>
/// A tenant-pinned connection is named after its tenant in <c>application_name</c>
/// (<see cref="HistoryHorizon.SessionTag"/>), so a transaction it holds open holds back only that
/// tenant's v3 history. Any other connection carries the connection string's own name, as a fresh
/// session does.
/// </para>
/// <para>
/// On the first open against a connection string the interceptor verifies that the connected role
/// is neither a superuser nor has BYPASSRLS. Both attributes silently defeat Row Level Security, so
/// Nocturne refuses to start when it detects them. The result is cached per connection string.
/// </para>
/// </remarks>
public class TenantConnectionInterceptor : DbConnectionInterceptor
{
    private const string SessionSql = """
        SELECT set_config('app.current_tenant_id', @tenant_id, false),
               set_config('app.current_subject_id', @subject_id, false),
               set_config('application_name', @application_name, false),
               set_config('app.is_share', @is_share, false),
               set_config('app.visible_categories', @visible_categories, false),
               set_config('app.share_full_history', @share_full_history, false),
               set_config('app.history_clamped', @history_clamped, false)
        """;

    private readonly ConcurrentDictionary<string, string> _verifiedApplicationNames = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
        => OpenedAsync(connection, eventData, async: true, cancellationToken);

    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => OpenedAsync(connection, eventData, async: false, CancellationToken.None).GetAwaiter().GetResult();

    private async Task OpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        bool async,
        CancellationToken cancellationToken)
    {
        var written = false;
        try
        {
            await WriteSessionAsync(connection, eventData, async, cancellationToken);
            written = true;
        }
        finally
        {
            if (!written)
            {
                if (async)
                {
                    await connection.CloseAsync();
                }
                else
                {
                    connection.Close();
                }
            }
        }
    }

    private async Task WriteSessionAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        bool async,
        CancellationToken cancellationToken)
    {
        var applicationName = await VerifyRoleAsync(connection, async, cancellationToken);

        if (eventData.Context is not NocturneDbContext ctx)
        {
            return;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = SessionSql;
        AddParameter(cmd, "tenant_id", Pin(ctx.TenantId));
        AddParameter(cmd, "subject_id", Pin(ctx.SubjectId));
        AddParameter(cmd, "application_name",
            ctx.TenantId == Guid.Empty ? applicationName : HistoryHorizon.SessionTag(ctx.TenantId));
        AddParameter(cmd, "is_share", ctx.IsShareContext ? "true" : "false");
        AddParameter(cmd, "visible_categories", ctx.VisibleCategories ?? string.Empty);
        AddParameter(cmd, "share_full_history", ctx.ShareFullHistory ? "true" : "false");
        AddParameter(cmd, "history_clamped", ctx.HistoryClamped ? "true" : "false");

        if (async)
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            cmd.ExecuteNonQuery();
        }
    }

    private static string Pin(Guid id) => id == Guid.Empty ? string.Empty : id.ToString();

    private static void AddParameter(DbCommand cmd, string name, string value)
    {
        var param = cmd.CreateParameter();
        param.ParameterName = name;
        param.Value = value;
        cmd.Parameters.Add(param);
    }

    /// <returns>The connection string's own <c>application_name</c>, or the empty string.</returns>
    private async Task<string> VerifyRoleAsync(DbConnection connection, bool async, CancellationToken cancellationToken)
    {
        var key = connection.ConnectionString ?? string.Empty;
        if (_verifiedApplicationNames.TryGetValue(key, out var applicationName))
        {
            return applicationName;
        }

        // If the check itself fails (network, permission denied on pg_roles, etc.)
        // we let the exception propagate. Do NOT cache and do NOT silently proceed.
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT current_user, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user";

        string user;
        bool isSuper;
        bool bypassRls;
        await using (var reader = async ? await cmd.ExecuteReaderAsync(cancellationToken) : cmd.ExecuteReader())
        {
            if (!(async ? await reader.ReadAsync(cancellationToken) : reader.Read()))
            {
                throw new InvalidOperationException(
                    "Role attribute self-check could not determine the current database user.");
            }

            user = reader.GetString(0);
            isSuper = reader.GetBoolean(1);
            bypassRls = reader.GetBoolean(2);
        }

        if (isSuper || bypassRls)
        {
            throw new InvalidOperationException(
                $"Database role '{user}' bypasses Row Level Security (superuser={isSuper}, bypassrls={bypassRls}). " +
                "Nocturne refuses to start with this role. Use 'nocturne_app' for the runtime connection string.");
        }

        applicationName = new NpgsqlConnectionStringBuilder(key).ApplicationName ?? string.Empty;
        _verifiedApplicationNames.TryAdd(key, applicationName);
        return applicationName;
    }
}
