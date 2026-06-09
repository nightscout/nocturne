using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Nocturne.Infrastructure.Data.Interceptors;

/// <summary>
/// EF Core connection interceptor that sets the PostgreSQL session variable
/// for Row-Level Security tenant isolation.
///
/// On connection open: SELECT set_config('app.current_tenant_id', $1, false)
/// On connection close: RESET app.current_tenant_id
///
/// Additionally, on the first open against any given connection string, the
/// interceptor verifies that the connected role is neither a superuser nor
/// has BYPASSRLS. Both attributes silently defeat Row Level Security, so
/// Nocturne refuses to start when it detects them. The check result is
/// cached per connection string so it is paid exactly once per data source.
///
/// This class is safe to register as a singleton. All mutable state is
/// confined to a <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// </summary>
public class TenantConnectionInterceptor : DbConnectionInterceptor
{
    private readonly ConcurrentDictionary<string, bool> _verifiedRoles = new(StringComparer.Ordinal);

    /// <summary>
    /// Executes asynchronously when a connection is opened.
    /// Verifies the role attributes (once per connection string) and sets
    /// the PostgreSQL session variable for tenant isolation when a tenant
    /// is in scope.
    /// </summary>
    /// <param name="connection">The database connection.</param>
    /// <param name="eventData">Information about the connection event.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await EnsureRoleIsSafeAsync(connection, cancellationToken);

        if (eventData.Context is not NocturneDbContext ctx)
        {
            return;
        }

        await using var cmd = connection.CreateCommand();
        var clauses = new List<string>(3);

        if (ctx.TenantId != Guid.Empty)
        {
            clauses.Add("set_config('app.current_tenant_id', @tenant_id, false)");
            AddParameter(cmd, "tenant_id", ctx.TenantId.ToString());
        }

        // app.is_share and app.visible_categories gate the per-category public-share RLS
        // policies. is_share is set on every open so a pooled connection never inherits a
        // previous lessee's share flag; for a share, a missing/empty visible_categories
        // denies all categorized data (fail-closed).
        clauses.Add("set_config('app.is_share', @is_share, false)");
        AddParameter(cmd, "is_share", ctx.IsShareContext ? "true" : "false");

        clauses.Add("set_config('app.visible_categories', @visible_categories, false)");
        AddParameter(cmd, "visible_categories", ctx.VisibleCategories ?? string.Empty);

        cmd.CommandText = "SELECT " + string.Join(", ", clauses);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand cmd, string name, string value)
    {
        var param = cmd.CreateParameter();
        param.ParameterName = name;
        param.Value = value;
        cmd.Parameters.Add(param);
    }

    /// <summary>
    /// Executes before a connection is closed.
    /// Resets the PostgreSQL session variable.
    /// </summary>
    /// <param name="connection">The database connection.</param>
    /// <param name="eventData">Information about the connection event.</param>
    /// <param name="result">The interception result.</param>
    /// <returns>The interception result.</returns>
    public override async ValueTask<InterceptionResult> ConnectionClosingAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result)
    {
        // Reset the session variable before the connection returns to the pool.
        // This prevents a stale tenant ID from leaking to the next request.
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "RESET app.current_tenant_id; RESET app.is_share; RESET app.visible_categories";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Swallow errors during cleanup - the connection may already be broken
        }

        return result;
    }

    private async Task EnsureRoleIsSafeAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var key = connection.ConnectionString ?? string.Empty;
        if (_verifiedRoles.ContainsKey(key))
        {
            return;
        }

        // If the check itself fails (network, permission denied on pg_roles, etc.)
        // we let the exception propagate. Do NOT cache and do NOT silently proceed.
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT current_user, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user";

        string user;
        bool isSuper;
        bool bypassRls;
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
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

        _verifiedRoles.TryAdd(key, true);
    }
}
